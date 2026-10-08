namespace TrafficControl.Api.Models;

// Request bodies. Everything is nullable on purpose: the controller validates and returns a clear 400
// instead of letting a missing field become a confusing default value.

public sealed class SensorEventRequest
{
    public string? EventId { get; set; }
    public string? JunctionId { get; set; }
    public string? Direction { get; set; }
    public string? EventType { get; set; }
    public string? VehicleId { get; set; }
    public string? VehicleType { get; set; }
    public long? SequenceNo { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
}

public sealed class CommandRequest
{
    public string? Command { get; set; }
    public string? Direction { get; set; }
    public string? RequestedBy { get; set; }
}

public sealed class ControllerEventRequest
{
    public string? CommandId { get; set; }
    public string? JunctionId { get; set; }
    public string? Status { get; set; }                       // ACK (default) | NACK | FAILED
    public string? ActualState { get; set; }                  // as in the PDF example
    public string? Direction { get; set; }                    // optional: which direction ActualState refers to
    public Dictionary<string, string>? ActualSignals { get; set; }   // optional full per-direction report
}

public sealed class DeviceStatusRequest
{
    public string? EventId { get; set; }
    public string? JunctionId { get; set; }
    public string? DeviceType { get; set; }                   // SIGNAL_CONTROLLER | SIGNAL | SENSOR
    public string? Direction { get; set; }
    public string? Status { get; set; }                       // ONLINE | OFFLINE | DEGRADED | WARNING | UNKNOWN
    public DateTimeOffset? Timestamp { get; set; }
}

public sealed class CreateJunctionRequest
{
    public string? JunctionId { get; set; }
    public string? Name { get; set; }
}

public sealed class AutoAckRequest
{
    public bool AutoAck { get; set; }
}

public sealed record ResultDto(string Outcome, string Message);
