using Microsoft.AspNetCore.Mvc;
using TrafficControl.Api.Models;
using TrafficControl.Application;
using TrafficControl.Domain;

namespace TrafficControl.Api.Controllers;

[ApiController]
[Route("api/sensor-events")]
public sealed class SensorEventsController(TrafficService traffic) : ControllerBase
{
    /// <summary>
    /// Idempotent: posting the same event_id again returns 200 DUPLICATE and changes nothing.
    /// 400 = malformed/missing/unknown values, 404 = unknown junction, 422 = understood but rejected (e.g. too old).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Post(SensorEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EventId)) ModelState.AddModelError("event_id", "event_id is required.");
        if (string.IsNullOrWhiteSpace(request.JunctionId)) ModelState.AddModelError("junction_id", "junction_id is required.");
        if (string.IsNullOrWhiteSpace(request.VehicleId)) ModelState.AddModelError("vehicle_id", "vehicle_id is required.");
        if (request.SequenceNo is null) ModelState.AddModelError("sequence_no", "sequence_no is required.");
        if (request.Timestamp is null) ModelState.AddModelError("timestamp", "timestamp is required (ISO 8601).");

        if (!Wire.TryParse<Direction>(request.Direction, out var direction))
            ModelState.AddModelError("direction", $"Unknown direction '{request.Direction}'. Use NORTH, SOUTH, EAST or WEST.");
        if (!Wire.TryParse<SensorEventType>(request.EventType, out var eventType))
            ModelState.AddModelError("event_type", $"Unknown event_type '{request.EventType}'. Use VEHICLE_ARRIVED or VEHICLE_CLEARED.");

        VehicleType? vehicleType = null;
        if (!string.IsNullOrWhiteSpace(request.VehicleType))
        {
            if (Wire.TryParse<VehicleType>(request.VehicleType, out var parsed))
                vehicleType = parsed;
            else
                ModelState.AddModelError("vehicle_type", $"Unknown vehicle_type '{request.VehicleType}'. Use FORKLIFT, TRUCK, EMPLOYEE_VEHICLE or EMERGENCY.");
        }
        if (ModelState.IsValid && eventType == SensorEventType.VehicleArrived && vehicleType is null)
            ModelState.AddModelError("vehicle_type", "vehicle_type is required for VEHICLE_ARRIVED.");

        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var result = await traffic.ProcessSensorEventAsync(new SensorEvent(
            request.EventId!, request.JunctionId!, direction, eventType,
            request.VehicleId!, vehicleType, request.SequenceNo!.Value, request.Timestamp!.Value));

        return ResultMapping.Map(this, result);
    }
}
