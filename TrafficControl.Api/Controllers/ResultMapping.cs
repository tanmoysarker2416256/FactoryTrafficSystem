using Microsoft.AspNetCore.Mvc;
using TrafficControl.Api.Models;
using TrafficControl.Domain;

namespace TrafficControl.Api.Controllers;

/// <summary>One place that decides which engine outcome becomes which HTTP status code.</summary>
public static class ResultMapping
{
    public static IActionResult Map(ControllerBase c, EngineResult r, bool acceptedAs202 = false) => r.Outcome switch
    {
        Outcome.Accepted when acceptedAs202 => c.Accepted(new ResultDto("ACCEPTED", r.Message)),
        Outcome.Accepted => c.Ok(new ResultDto("ACCEPTED", r.Message)),
        Outcome.Duplicate => c.Ok(new ResultDto("DUPLICATE", r.Message)),        // idempotent: same answer, no state change
        Outcome.Conflict => c.Conflict(new ResultDto("CONFLICT", r.Message)),    // valid request, not allowed in the current state
        _ => c.UnprocessableEntity(new ResultDto("REJECTED", r.Message))         // understood but invalid
    };
}
