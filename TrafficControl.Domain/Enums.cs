namespace TrafficControl.Domain;

public enum Direction { North, South, East, West }

/// <summary>What the physical lamp shows. Unknown = the backend has no trustworthy confirmation.</summary>
public enum SignalState { Unknown, Red, Yellow, Green }

public enum VehicleType { Employee, Forklift, Truck, Emergency }

public enum OperatingMode { Automatic, Manual, Emergency, Degraded }

/// <summary>Where the junction is inside the safe sequence GREEN -> YELLOW -> ALL_RED -> GREEN.</summary>
public enum SignalStage { Green, Yellow, AllRed }

public enum SensorEventType { VehicleArrived, VehicleCleared }

public enum AdminCommandType { ManualGreenRequest, ReturnToAutomatic }

public enum DeviceType { SignalController, Signal, Sensor }

public enum DeviceStatus { Online, Offline, Degraded, Warning, Unknown }

public enum AckStatus { Ack, Nack, Failed }

public enum CommandStatus { Pending, Acknowledged, Failed, TimedOut, Superseded, Unknown }

/// <summary>
/// Accepted  -> applied (HTTP 200/202)
/// Duplicate -> already processed, nothing changed (HTTP 200, idempotent)
/// Rejected  -> invalid input (HTTP 400/422)
/// Conflict  -> valid input but not allowed in the current state (HTTP 409)
/// </summary>
public enum Outcome { Accepted, Duplicate, Rejected, Conflict }

public enum AuditEventType
{
    VehicleDetected, VehicleCleared, EventRejected, DuplicateEventRejected, SequenceAnomaly,
    TransitionStarted, SignalRequested, SignalConfirmed,
    EmergencyDetected, EmergencyCleared,
    ManualOverride, ManualExpired, ReturnToAutomatic, CommandRejected,
    ControllerAck, AckIgnored, ControllerTimeout, DeviceFailure, DeviceRecovered,
    ModeChanged, Recovery
}
