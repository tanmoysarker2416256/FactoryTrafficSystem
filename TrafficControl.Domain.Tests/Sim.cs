using TrafficControl.Domain;

namespace TrafficControl.Domain.Tests;

/// <summary>
/// Test harness = a fake world around the engine: fake clock, fake controller that ACKs instantly (or never).
/// No HTTP, no database, no MQTT, no sleeping. A full 60-second traffic cycle runs in milliseconds.
/// </summary>
public sealed class Sim
{
    private readonly Queue<SendControllerCommand> _unacked = new();
    private int _ids, _evt;
    private long _seq;

    public DateTimeOffset Now { get; private set; } = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    public JunctionEngine Engine { get; }
    public bool AutoAck { get; set; }
    public List<SendControllerCommand> Sent { get; } = new();
    public List<AuditEntry> Audit { get; } = new();

    public JunctionState State => Engine.State;

    public Sim(bool autoAck = true, JunctionConfig? cfg = null)
    {
        AutoAck = autoAck;
        cfg ??= new JunctionConfig();
        Engine = new JunctionEngine(cfg, JunctionState.CreateInitial(cfg), () => $"cmd-{++_ids}");
        Run(Engine.Start(Now));
    }

    public EngineResult Run(EngineResult result)
    {
        Collect(result);
        while (AutoAck && _unacked.Count > 0)
        {
            var c = _unacked.Dequeue();
            Collect(Engine.HandleControllerAck(new ControllerAck(c.CommandId, c.JunctionId, AckStatus.Ack, null), Now));
        }
        return result;
    }

    private void Collect(EngineResult result)
    {
        foreach (var fx in result.Effects)
        {
            switch (fx)
            {
                case SendControllerCommand c: Sent.Add(c); _unacked.Enqueue(c); break;
                case WriteAudit a: Audit.Add(a.Entry); break;
            }
        }
    }

    public void Advance(TimeSpan span, TimeSpan? step = null)
    {
        var s = step ?? TimeSpan.FromMilliseconds(500);
        var end = Now + span;
        while (Now < end)
        {
            Now += s;
            Run(Engine.Tick(Now));
        }
    }

    public void Advance(int seconds) => Advance(TimeSpan.FromSeconds(seconds));

    public EngineResult Arrive(Direction d, VehicleType t, string? vehicleId = null, string? eventId = null)
    {
        var id = eventId ?? $"evt-{++_evt}";
        return Run(Engine.HandleSensorEvent(
            new SensorEvent(id, "A", d, SensorEventType.VehicleArrived, vehicleId ?? $"VH-{_evt}", t, ++_seq, Now), Now));
    }

    public EngineResult Clear(Direction d, string vehicleId, string? eventId = null) =>
        Run(Engine.HandleSensorEvent(
            new SensorEvent(eventId ?? $"evt-{++_evt}", "A", d, SensorEventType.VehicleCleared, vehicleId, null, ++_seq, Now), Now));

    public EngineResult Admin(AdminCommand c) => Run(Engine.HandleAdminCommand(c, Now));

    public EngineResult Device(DeviceType type, Direction? d, DeviceStatus status) =>
        Run(Engine.HandleDeviceStatus(new DeviceStatusEvent($"st-{++_evt}", "A", type, d, status, Now), Now));

    public int QueueCount(Direction d) => State.Queues[d].Count;
    public SignalState Desired(Direction d) => State.Desired[d];

    /// <summary>Distinct commands (retries share a command_id) sent after the given index.</summary>
    public List<SendControllerCommand> NewCommands(int since) =>
        Sent.Skip(since).GroupBy(c => c.CommandId).Select(g => g.First()).ToList();
}
