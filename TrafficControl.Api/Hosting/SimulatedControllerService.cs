using TrafficControl.Application;
using TrafficControl.Domain;
using TrafficControl.Infrastructure;

namespace TrafficControl.Api.Hosting;

/// <summary>
/// The "physical controller" for demos. Reads commands the backend sent, waits a moment, and ACKs them
/// through the same application path a real controller (REST or MQTT) would use.
/// With auto-ACK off the commands are simply never answered.
/// </summary>
public sealed class SimulatedControllerService(
    SimulatedController controller, TrafficService traffic, ILogger<SimulatedControllerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var command in controller.Reader.ReadAllAsync(stoppingToken))
            {
                if (!controller.AutoAck) continue;
                try
                {
                    await Task.Delay(controller.AckDelay, stoppingToken);
                    if (!controller.AutoAck) continue;
                    await traffic.ProcessControllerAckAsync(new ControllerAckInput(command.CommandId, command.JunctionId, AckStatus.Ack));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Simulated ACK for {CommandId} failed", command.CommandId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
