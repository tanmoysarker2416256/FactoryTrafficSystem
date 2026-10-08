using TrafficControl.Domain;

namespace TrafficControl.Application;

/// <summary>
/// Controller acknowledgement as received from the outside world (REST today, MQTT later).
/// ActualSignals  : full per-direction report (most precise).
/// ActualState    : single state, as in the PDF example. With TargetDirection it applies to that direction;
///                  without it, it applies to the directions the command asked to be non-RED (or to all, for an all-red command).
/// Neither given  : the controller confirms exactly what was requested.
/// </summary>
public sealed record ControllerAckInput(
    string CommandId,
    string JunctionId,
    AckStatus Status,
    IReadOnlyDictionary<Direction, SignalState>? ActualSignals = null,
    SignalState? ActualState = null,
    Direction? TargetDirection = null);
