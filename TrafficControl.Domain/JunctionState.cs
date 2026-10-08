namespace TrafficControl.Domain;

public sealed class QueuedVehicle
{
    public string VehicleId { get; set; } = "";
    public VehicleType Type { get; set; }
    public DateTimeOffset ArrivedAt { get; set; }
}

public sealed class EmergencyRequest
{
    public string VehicleId { get; set; } = "";
    public Direction Direction { get; set; }
    public DateTimeOffset DetectedAt { get; set; }   // server time: sensor clocks are not trusted for ordering emergencies
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class CommandRecord
{
    public string CommandId { get; set; } = "";
    public Dictionary<Direction, SignalState> Signals { get; set; } = new();
    public int Attempt { get; set; } = 1;
    public DateTimeOffset SentAt { get; set; }
    public string Purpose { get; set; } = "";
}

public sealed class Alert
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public Direction? Direction { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
}

public static class AlertCodes
{
    public const string ControllerOffline = "CONTROLLER_OFFLINE";
    public const string Degraded = "JUNCTION_DEGRADED";
    public const string CommandTimeout = "COMMAND_TIMEOUT";
    public const string StateMismatch = "STATE_MISMATCH";
    public const string UnsafeActual = "UNSAFE_ACTUAL_STATE";
    public const string SignalFailure = "SIGNAL_FAILURE";
    public const string SensorOffline = "SENSOR_OFFLINE";
}

/// <summary>
/// Everything that must survive a restart. Plain properties only, so it serialises to JSON in one line
/// (the Infrastructure layer stores it as a snapshot row). Anything not here can be rebuilt.
/// </summary>
public sealed class JunctionState
{
    public string JunctionId { get; set; } = "";
    public long Version { get; set; }
    public DateTimeOffset LastUpdated { get; set; }

    public OperatingMode Mode { get; set; } = OperatingMode.Automatic;
    public SignalStage Stage { get; set; } = SignalStage.AllRed;
    public string? ActivePhaseId { get; set; }
    public string? TargetPhaseId { get; set; }
    public DateTimeOffset StageEnteredAt { get; set; }

    /// <summary>What the backend WANTS.</summary>
    public Dictionary<Direction, SignalState> Desired { get; set; } = new();
    /// <summary>What the physical controller CONFIRMED. Unknown until an ACK arrives.</summary>
    public Dictionary<Direction, SignalState> Actual { get; set; } = new();

    public Dictionary<Direction, List<QueuedVehicle>> Queues { get; set; } = new();
    public Dictionary<string, DateTimeOffset> ClearedTombstones { get; set; } = new();
    public Dictionary<Direction, long> LastSequence { get; set; } = new();
    public List<string> ProcessedEventIds { get; set; } = new();

    public List<EmergencyRequest> Emergencies { get; set; } = new();
    public string? ManualPhaseId { get; set; }
    public DateTimeOffset? ManualExpiresAt { get; set; }

    public CommandRecord? Pending { get; set; }
    public Dictionary<string, CommandStatus> CommandHistory { get; set; } = new();
    public List<string> CommandOrder { get; set; } = new();

    public DeviceStatus ControllerStatus { get; set; } = DeviceStatus.Unknown;
    public HashSet<Direction> OfflineSensors { get; set; } = new();
    public DateTimeOffset LastProbeAt { get; set; }
    public List<Alert> Alerts { get; set; } = new();

    /// <summary>Fail-safe starting point: desired = all red, actual = unknown.</summary>
    public static JunctionState CreateInitial(JunctionConfig cfg)
    {
        var s = new JunctionState { JunctionId = cfg.JunctionId };
        foreach (var d in cfg.AllDirections)
        {
            s.Desired[d] = SignalState.Red;
            s.Actual[d] = SignalState.Unknown;
            s.Queues[d] = new List<QueuedVehicle>();
        }
        return s;
    }
}
