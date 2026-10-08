using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TrafficControl.Domain;

namespace TrafficControl.Application;

/// <summary>
/// ONE actor per junction = our consistency strategy ("serialized per-junction processing").
///
/// Every input (sensor event, admin command, ACK, timer tick, even status reads) is a message in an in-memory queue.
/// A single loop takes one message at a time. So two requests can never run engine code concurrently,
/// and the engine itself needs no locks.
///
/// Order of work for every message that changes something:
///   1) run the engine (pure, deterministic)
///   2) persist snapshot + audit rows in ONE transaction     (write-ahead)
///   3) only then talk to the physical controller
/// If we crash between 2 and 3 the restart logic treats the command as Unknown and re-establishes ALL_RED.
/// If 2 fails, the controller never hears about the decision and we reload the last persisted state.
/// </summary>
public sealed class JunctionActor
{
    private readonly Channel<Work> _inbox = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly JunctionConfig _config;
    private readonly IJunctionStore _store;
    private readonly IControllerGateway _gateway;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private JunctionEngine _engine;

    public JunctionActor(JunctionConfig config, JunctionState state, IJunctionStore store,
        IControllerGateway gateway, IClock clock, ILogger log)
    {
        _config = config;
        _store = store;
        _gateway = gateway;
        _clock = clock;
        _log = log;
        _engine = new JunctionEngine(config, state);
    }

    public string JunctionId => _config.JunctionId;

    /// <summary>The message loop. Runs for the lifetime of the application.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var work in _inbox.Reader.ReadAllAsync(ct))
                await work.RunAsync(this);
        }
        catch (OperationCanceledException)
        {
            // application is shutting down
        }
    }

    /// <summary>A message that may change state (sensor event, command, ACK, tick).</summary>
    public Task<EngineResult> SendAsync(Func<JunctionEngine, DateTimeOffset, EngineResult> action)
    {
        var work = new CommandWork(action);
        if (!_inbox.Writer.TryWrite(work)) throw new InvalidOperationException("Junction actor is stopped.");
        return work.Completion.Task;
    }

    /// <summary>A read. Also goes through the queue, so it never observes a half-finished update.</summary>
    public Task<T> QueryAsync<T>(Func<JunctionEngine, DateTimeOffset, T> query)
    {
        var work = new QueryWork<T>(query);
        if (!_inbox.Writer.TryWrite(work)) throw new InvalidOperationException("Junction actor is stopped.");
        return work.Completion.Task;
    }

    private async Task<EngineResult> ExecuteAsync(Func<JunctionEngine, DateTimeOffset, EngineResult> action)
    {
        try
        {
            var result = action(_engine, _clock.UtcNow);

            if (result.StateChanged)
            {
                var audits = result.Effects.OfType<WriteAudit>().Select(a => a.Entry).ToList();
                await _store.SaveAsync(JunctionId, _engine.State, audits);       // 2) persist first
            }

            foreach (var send in result.Effects.OfType<SendControllerCommand>())  // 3) then talk to the controller
            {
                try
                {
                    await _gateway.SendAsync(send);
                }
                catch (Exception ex)
                {
                    // A failed send is the same as a lost message: the engine's ACK timeout will retry and eventually degrade.
                    _log.LogWarning(ex, "Sending {CommandId} to the controller failed; ACK timeout will handle it", send.CommandId);
                }
            }
            return result;
        }
        catch
        {
            // Memory may now be ahead of the database (or, in the worst case, unsafe). Go back to the last persisted state
            // and re-enter fail-safe recovery instead of continuing with state nobody has stored.
            await ReloadFromStoreAsync();
            throw;
        }
    }

    private async Task ReloadFromStoreAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));    // rate-limit: if the database is down we must not spin
            var stored = await _store.GetAsync(JunctionId);
            if (stored is null) return;

            _engine = new JunctionEngine(_config, stored.State);
            _inbox.Writer.TryWrite(new CommandWork((engine, now) => engine.Start(now)));
            _log.LogWarning("Junction {JunctionId}: reloaded last persisted state and scheduled recovery", JunctionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Junction {JunctionId}: could not reload state from the store", JunctionId);
        }
    }

    // ---- queue items -------------------------------------------------------------------------------------------

    private abstract class Work
    {
        public abstract Task RunAsync(JunctionActor actor);
    }

    private sealed class CommandWork(Func<JunctionEngine, DateTimeOffset, EngineResult> action) : Work
    {
        public TaskCompletionSource<EngineResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task RunAsync(JunctionActor actor)
        {
            try { Completion.SetResult(await actor.ExecuteAsync(action)); }
            catch (Exception ex) { Completion.SetException(ex); }
        }
    }

    private sealed class QueryWork<T>(Func<JunctionEngine, DateTimeOffset, T> query) : Work
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task RunAsync(JunctionActor actor)
        {
            try { Completion.SetResult(query(actor._engine, actor._clock.UtcNow)); }
            catch (Exception ex) { Completion.SetException(ex); }
            return Task.CompletedTask;
        }
    }
}
