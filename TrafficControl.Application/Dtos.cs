namespace TrafficControl.Application;

// Read models returned by the API. Property names become snake_case in JSON (configured in the API project).

public sealed record AlertDto(string Code, string Message, string? Direction, DateTimeOffset RaisedAt);

public sealed record VehicleDto(string VehicleId, string VehicleType, double WaitingSeconds);

public sealed record EmergencyDto(string VehicleId, string Direction, DateTimeOffset DetectedAt);

public sealed record PendingCommandDto(
    string CommandId, string Purpose, int Attempt, DateTimeOffset SentAt, IReadOnlyDictionary<string, string> Signals);

public sealed record ManualDto(string Phase, DateTimeOffset ExpiresAt);

public sealed record JunctionStatusDto(
    string JunctionId,
    string Name,
    string Mode,
    string Phase,
    string Stage,
    string? TargetPhase,
    string ControllerStatus,
    IReadOnlyDictionary<string, string> DesiredSignals,
    IReadOnlyDictionary<string, string> ActualSignals,
    IReadOnlyDictionary<string, int> Queues,
    IReadOnlyDictionary<string, IReadOnlyList<VehicleDto>> QueueVehicles,
    bool DesiredActualMismatch,
    PendingCommandDto? PendingCommand,
    IReadOnlyList<EmergencyDto> Emergencies,
    ManualDto? Manual,
    IReadOnlyList<string> OfflineSensors,
    IReadOnlyList<AlertDto> Alerts,
    double StageElapsedSeconds,
    DateTimeOffset UpdatedAt);

public sealed record PhaseDto(string Id, IReadOnlyList<string> Directions);

public sealed record JunctionDetailDto(
    string JunctionId,
    string Name,
    IReadOnlyList<PhaseDto> Phases,
    IReadOnlyDictionary<string, double> TimingSeconds,
    JunctionStatusDto Status);

public sealed record AuditEntryDto(
    string JunctionId, string EventType, string Message, string? Direction,
    string? PreviousState, string? NewState, string? CommandId, DateTimeOffset Timestamp);
