namespace TrafficControl.Domain;

// ---------- inputs (already parsed/typed by the API layer) ----------

public sealed record SensorEvent(
    string EventId, string JunctionId, Direction Direction, SensorEventType EventType,
    string VehicleId, VehicleType? VehicleType, long SequenceNo, DateTimeOffset Timestamp);

public sealed record AdminCommand(AdminCommandType Type, Direction? Direction, string? RequestedBy = null);

/// <summary>ActualSignals == null means "the controller confirmed exactly what was requested".</summary>
public sealed record ControllerAck(
    string CommandId, string JunctionId, AckStatus Status, IReadOnlyDictionary<Direction, SignalState>? ActualSignals);

public sealed record DeviceStatusEvent(
    string EventId, string JunctionId, DeviceType DeviceType, Direction? Direction, DeviceStatus Status, DateTimeOffset Timestamp);

// ---------- outputs ----------

public sealed record AuditEntry(
    string JunctionId, AuditEventType EventType, string Message, DateTimeOffset Timestamp,
    Direction? Direction = null, string? PreviousState = null, string? NewState = null, string? CommandId = null);

/// <summary>Side effects the engine asks the outside world to perform. The engine itself never does I/O.</summary>
public abstract record DomainEffect;

public sealed record SendControllerCommand(
    string CommandId, string JunctionId, IReadOnlyDictionary<Direction, SignalState> Signals, int Attempt) : DomainEffect;

public sealed record WriteAudit(AuditEntry Entry) : DomainEffect;

public sealed record EngineResult(Outcome Outcome, string Message, IReadOnlyList<DomainEffect> Effects)
{
    /// <summary>The application layer persists a snapshot only when something actually happened.</summary>
    public bool StateChanged => Effects.Count > 0;
}

/// <summary>Thrown only if the engine itself is about to produce an unsafe state. This is a bug, never user input.</summary>
public sealed class SafetyViolationException(string message) : Exception(message);
