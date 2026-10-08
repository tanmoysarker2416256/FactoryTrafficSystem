using TrafficControl.Application;

namespace TrafficControl.Api.Hosting;

/// <summary>
/// 1) On startup: load junctions from the database and run recovery (see TrafficService.InitializeAsync).
/// 2) Then every 500 ms send a Tick to every junction. Signal timing is driven by this timer,
///    never by sleeping inside a request handler.
/// </summary>
public sealed class TrafficRuntimeService(TrafficService traffic, ILogger<TrafficRuntimeService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await traffic.InitializeAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Traffic runtime could not start (is the database reachable?)");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await traffic.TickAllAsync();
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
