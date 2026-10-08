using Microsoft.AspNetCore.Mvc;
using TrafficControl.Api.Models;
using TrafficControl.Application;
using TrafficControl.Domain;

namespace TrafficControl.Api.Controllers;

[ApiController]
[Route("api/junctions")]
public sealed class JunctionsController(TrafficService traffic) : ControllerBase
{
    /// <summary>All junctions with their live status (used by the dashboard overview).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<JunctionStatusDto>>> List() => Ok(await traffic.ListAsync());

    /// <summary>Junction configuration (phases, timings) plus its current status.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<JunctionDetailDto>> Get(string id) => Ok(await traffic.GetDetailAsync(id));

    [HttpPost]
    public async Task<IActionResult> Create(CreateJunctionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JunctionId))
        {
            ModelState.AddModelError("junction_id", "junction_id is required.");
            return ValidationProblem(ModelState);
        }
        var status = await traffic.CreateJunctionAsync(request.JunctionId, request.Name);
        return CreatedAtAction(nameof(Get), new { id = status.JunctionId }, status);
    }

    /// <summary>Desired vs actual signals, queues, mode, phase, alerts, pending command.</summary>
    [HttpGet("{id}/status")]
    public async Task<ActionResult<JunctionStatusDto>> Status(string id) => Ok(await traffic.GetStatusAsync(id));

    /// <summary>Newest first. Audit trail that explains why the junction is in its current state.</summary>
    [HttpGet("{id}/history")]
    public async Task<ActionResult<IReadOnlyList<AuditEntryDto>>> History(string id, [FromQuery] int limit = 50) =>
        Ok(await traffic.GetHistoryAsync(id, limit));

    /// <summary>
    /// Command-oriented control: the client states an INTENT (MANUAL_GREEN_REQUEST / RETURN_TO_AUTOMATIC).
    /// It can never write a signal state directly; the engine decides the safe transition.
    /// </summary>
    [HttpPost("{id}/commands")]
    public async Task<IActionResult> Command(string id, CommandRequest request)
    {
        if (!Wire.TryParse<AdminCommandType>(request.Command, out var type))
        {
            ModelState.AddModelError("command", $"Unknown command '{request.Command}'. Supported: MANUAL_GREEN_REQUEST, RETURN_TO_AUTOMATIC.");
            return ValidationProblem(ModelState);
        }

        Direction? direction = null;
        if (!string.IsNullOrWhiteSpace(request.Direction))
        {
            if (Wire.TryParse<Direction>(request.Direction, out var parsed))
            {
                direction = parsed;
            }
            else
            {
                ModelState.AddModelError("direction", $"Unknown direction '{request.Direction}'.");
                return ValidationProblem(ModelState);
            }
        }

        if (type == AdminCommandType.ManualGreenRequest && direction is null)
        {
            ModelState.AddModelError("direction", "direction is required for MANUAL_GREEN_REQUEST.");
            return ValidationProblem(ModelState);
        }

        var result = await traffic.ProcessAdminCommandAsync(id, new AdminCommand(type, direction, request.RequestedBy ?? "dashboard-admin"));
        return ResultMapping.Map(this, result, acceptedAs202: true);   // 202: the safe transition happens over the next seconds
    }
}
