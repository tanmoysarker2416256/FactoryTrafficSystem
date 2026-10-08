using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TrafficControl.Domain;

namespace TrafficControl.Application;

/// <summary>
/// Application layer: owns the junction actors and routes every request to the right one.
/// It contains no traffic rules (those are in the Domain) and no HTTP/SQL details (those are behind ports).
/// </summary>
public sealed class TrafficService
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, JunctionActor> _actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly IJunctionStore _store;
    private readonly IControllerGateway _gateway;
    private readonly IClock _clock;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<TrafficService> _log;
    private CancellationToken _stop;

    public TrafficService(IJunctionStore store, IControllerGateway gateway, IClock clock,
        ILoggerFactory loggers, ILogger<TrafficService> log)
    {
        _store = store;
        _gateway = gateway;
        _clock = clock;
        _loggers = loggers;
        _log = log;
    }

    // ------------------------------------------------------------------ startup / recovery

    /// <summary>
    /// Loads every junction from the database and starts its actor. Each junction is started through engine.Start(),
    /// which distrusts the old physical state, forces ALL_RED and waits for confirmation. This IS the restart-recovery path.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            _stop = ct;
            var stored = await _store.ListAsync(ct);
            if (stored.Count == 0)
            {
                var config = new JunctionConfig();                         // seed Junction A so the demo works out of the box
                await _store.CreateAsync(config, JunctionState.CreateInitial(config), ct);
                stored = await _store.ListAsync(ct);
            }

            foreach (var junction in stored)
                await RegisterAsync(junction.Config, junction.State);

            _log.LogInformation("Traffic runtime ready with {Count} junction(s)", stored.Count);
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            throw;
        }
    }

    private async Task RegisterAsync(JunctionConfig config, JunctionState state)
    {
        var actor = new JunctionActor(config, state, _store, _gateway, _clock, _loggers.CreateLogger($"Junction.{config.JunctionId}"));
        _actors[config.JunctionId] = actor;
        _ = Task.Run(() => actor.RunAsync(_stop), CancellationToken.None);
        await actor.SendAsync((engine, now) => engine.Start(now));
    }

    /// <summary>Called by the background ticker. Time passing is just another message.</summary>
    public async Task TickAllAsync()
    {
        foreach (var actor in _actors.Values)
        {
            try
            {
                await actor.SendAsync((engine, now) => engine.Tick(now));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Tick failed for junction {JunctionId}", actor.JunctionId);
            }
        }
    }

    // ------------------------------------------------------------------ junctions

    public async Task<IReadOnlyList<JunctionStatusDto>> ListAsync()
    {
        await _ready.Task;
        var result = new List<JunctionStatusDto>();
        foreach (var actor in _actors.Values.OrderBy(a => a.JunctionId, StringComparer.OrdinalIgnoreCase))
            result.Add(await actor.QueryAsync((engine, now) => StatusMapper.ToStatus(engine, now)));
        return result;
    }

    public async Task<JunctionStatusDto> GetStatusAsync(string junctionId)
    {
        var actor = await GetActorAsync(junctionId);
        return await actor.QueryAsync((engine, now) => StatusMapper.ToStatus(engine, now));
    }

    public async Task<JunctionDetailDto> GetDetailAsync(string junctionId)
    {
        var actor = await GetActorAsync(junctionId);
        return await actor.QueryAsync((engine, now) => StatusMapper.ToDetail(engine, now));
    }

    public async Task<JunctionStatusDto> CreateJunctionAsync(string junctionId, string? name)
    {
        await _ready.Task;
        if (!IdPattern.IsMatch(junctionId ?? ""))
            throw new RequestValidationException("junction_id must be 1-32 characters: letters, digits, '-' or '_'.");
        if (name is { Length: > 100 })
            throw new RequestValidationException("name must be at most 100 characters.");

        await _createLock.WaitAsync();
        try
        {
            if (_actors.ContainsKey(junctionId!)) throw new JunctionAlreadyExistsException(junctionId!);

            var config = new JunctionConfig
            {
                JunctionId = junctionId!,
                Name = string.IsNullOrWhiteSpace(name) ? $"Junction {junctionId}" : name.Trim()
            };
            var state = JunctionState.CreateInitial(config);
            await _store.CreateAsync(config, state);
            await RegisterAsync(config, state);
        }
        finally
        {
            _createLock.Release();
        }
        return await GetStatusAsync(junctionId!);
    }

    // ------------------------------------------------------------------ inputs

    public async Task<EngineResult> ProcessSensorEventAsync(SensorEvent e)
    {
        var actor = await GetActorAsync(e.JunctionId);
        return await actor.SendAsync((engine, now) => engine.HandleSensorEvent(e, now));
    }

    public async Task<EngineResult> ProcessAdminCommandAsync(string junctionId, AdminCommand command)
    {
        var actor = await GetActorAsync(junctionId);
        return await actor.SendAsync((engine, now) => engine.HandleAdminCommand(command, now));
    }

    public async Task<EngineResult> ProcessDeviceStatusAsync(DeviceStatusEvent e)
    {
        var actor = await GetActorAsync(e.JunctionId);
        return await actor.SendAsync((engine, now) => engine.HandleDeviceStatus(e, now));
    }

    public async Task<EngineResult> ProcessControllerAckAsync(ControllerAckInput input)
    {
        var actor = await GetActorAsync(input.JunctionId);
        return await actor.SendAsync((engine, now) =>
        {
            // Built inside the actor so we read the pending command consistently.
            var actual = BuildActualSignals(engine, input);
            return engine.HandleControllerAck(new ControllerAck(input.CommandId, input.JunctionId, input.Status, actual), now);
        });
    }

    public async Task<IReadOnlyList<AuditEntryDto>> GetHistoryAsync(string junctionId, int limit, CancellationToken ct = default)
    {
        await GetActorAsync(junctionId);   // 404 for unknown junctions
        var entries = await _store.GetHistoryAsync(junctionId, Math.Clamp(limit, 1, 500), ct);
        return entries.Select(StatusMapper.ToDto).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<JunctionActor> GetActorAsync(string? junctionId)
    {
        await _ready.Task;
        if (junctionId is null || !_actors.TryGetValue(junctionId, out var actor))
            throw new JunctionNotFoundException(junctionId ?? "");
        return actor;
    }

    private static IReadOnlyDictionary<Direction, SignalState>? BuildActualSignals(JunctionEngine engine, ControllerAckInput input)
    {
        if (input.ActualSignals is not null) return input.ActualSignals;
        if (input.ActualState is null) return null;

        var pending = engine.State.Pending;
        if (pending is null || pending.CommandId != input.CommandId) return null;   // duplicate/late ACK: engine will ignore it

        var actual = new Dictionary<Direction, SignalState>(pending.Signals);
        List<Direction> targets;
        if (input.TargetDirection is { } one)
        {
            targets = new List<Direction> { one };
        }
        else
        {
            targets = pending.Signals.Where(kv => kv.Value != SignalState.Red).Select(kv => kv.Key).ToList();
            if (targets.Count == 0) targets = pending.Signals.Keys.ToList();      // an all-red command
        }
        foreach (var t in targets) actual[t] = input.ActualState.Value;
        return actual;
    }
}
