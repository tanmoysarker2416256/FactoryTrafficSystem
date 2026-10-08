using Microsoft.AspNetCore.Mvc;
using TrafficControl.Api.Models;
using TrafficControl.Application;
using TrafficControl.Domain;

namespace TrafficControl.Api.Controllers;

[ApiController]
public sealed class ControllerEventsController(TrafficService traffic) : ControllerBase
{
    /// <summary>
    /// Controller acknowledgement (REST stand-in for the MQTT ACK topic).
    /// Correlated by command_id. A duplicate or late ACK returns 200 DUPLICATE and changes nothing.
    /// </summary>
    [HttpPost("api/controller-events")]
    public async Task<IActionResult> Ack(ControllerEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CommandId)) ModelState.AddModelError("command_id", "command_id is required.");
        if (string.IsNullOrWhiteSpace(request.JunctionId)) ModelState.AddModelError("junction_id", "junction_id is required.");

        var status = AckStatus.Ack;
        if (!string.IsNullOrWhiteSpace(request.Status) && !Wire.TryParse<AckStatus>(request.Status, out status))
            ModelState.AddModelError("status", $"Unknown status '{request.Status}'. Use ACK, NACK or FAILED.");

        SignalState? actualState = null;
        if (!string.IsNullOrWhiteSpace(request.ActualState))
        {
            if (Wire.TryParse<SignalState>(request.ActualState, out var s)) actualState = s;
            else ModelState.AddModelError("actual_state", $"Unknown actual_state '{request.ActualState}'.");
        }

        Direction? target = null;
        if (!string.IsNullOrWhiteSpace(request.Direction))
        {
            if (Wire.TryParse<Direction>(request.Direction, out var d)) target = d;
            else ModelState.AddModelError("direction", $"Unknown direction '{request.Direction}'.");
        }

        Dictionary<Direction, SignalState>? signals = null;
        if (request.ActualSignals is { Count: > 0 })
        {
            signals = new Dictionary<Direction, SignalState>();
            foreach (var (key, value) in request.ActualSignals)
            {
                if (Wire.TryParse<Direction>(key, out var dir) && Wire.TryParse<SignalState>(value, out var st)) signals[dir] = st;
                else ModelState.AddModelError("actual_signals", $"Invalid entry '{key}': '{value}'.");
            }
        }

        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var result = await traffic.ProcessControllerAckAsync(
            new ControllerAckInput(request.CommandId!, request.JunctionId!, status, signals, actualState, target));
        return ResultMapping.Map(this, result);
    }

    /// <summary>Device/sensor/controller status (the PDF's "status-301" example). Added endpoint, see README.</summary>
    [HttpPost("api/device-status")]
    public async Task<IActionResult> PostDeviceStatus(DeviceStatusRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EventId)) ModelState.AddModelError("event_id", "event_id is required.");
        if (string.IsNullOrWhiteSpace(request.JunctionId)) ModelState.AddModelError("junction_id", "junction_id is required.");

        if (!Wire.TryParse<DeviceType>(request.DeviceType, out var deviceType))
            ModelState.AddModelError("device_type", $"Unknown device_type '{request.DeviceType}'. Use SIGNAL_CONTROLLER, SIGNAL or SENSOR.");
        if (!Wire.TryParse<DeviceStatus>(request.Status, out var status))
            ModelState.AddModelError("status", $"Unknown status '{request.Status}'. Use ONLINE, OFFLINE, DEGRADED, WARNING or UNKNOWN.");

        Direction? direction = null;
        if (!string.IsNullOrWhiteSpace(request.Direction))
        {
            if (Wire.TryParse<Direction>(request.Direction, out var d)) direction = d;
            else ModelState.AddModelError("direction", $"Unknown direction '{request.Direction}'.");
        }

        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var result = await traffic.ProcessDeviceStatusAsync(new DeviceStatusEvent(
            request.EventId!, request.JunctionId!, deviceType, direction, status, request.Timestamp ?? DateTimeOffset.UtcNow));
        return ResultMapping.Map(this, result);
    }
}
