using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrafficControl.Application;

namespace TrafficControl.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Traffic")
            ?? throw new InvalidOperationException("Connection string 'Traffic' is missing in appsettings.json.");

        services.AddDbContextFactory<TrafficDbContext>(options => options.UseSqlServer(connectionString));
        services.AddSingleton<IJunctionStore, JunctionStore>();
        services.AddSingleton<IClock, SystemClock>();

        // The simulator is registered once and exposed through the port. Swap this line for an MQTT adapter later.
        services.AddSingleton<SimulatedController>();
        services.AddSingleton<IControllerGateway>(sp => sp.GetRequiredService<SimulatedController>());
        return services;
    }
}
