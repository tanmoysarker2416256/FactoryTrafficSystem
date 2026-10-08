using Microsoft.AspNetCore.Mvc;
using TrafficControl.Api.Models;
using TrafficControl.Infrastructure;

namespace TrafficControl.Api.Controllers;

/// <summary>Demo helper: switch the simulated controller's automatic ACKs on/off to demonstrate "ACK never received".</summary>
[ApiController]
[Route("api/simulator")]
public sealed class SimulatorController(SimulatedController controller) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { auto_ack = controller.AutoAck, ack_delay_ms = (int)controller.AckDelay.TotalMilliseconds });

    [HttpPost("auto-ack")]
    public IActionResult SetAutoAck(AutoAckRequest request)
    {
        controller.AutoAck = request.AutoAck;
        return Ok(new { auto_ack = controller.AutoAck });
    }
}
