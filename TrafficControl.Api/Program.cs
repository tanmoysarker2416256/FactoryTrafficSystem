using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrafficControl.Api.Hosting;
using TrafficControl.Api.Middleware;
using TrafficControl.Application;
using TrafficControl.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
    {
        // The PDF's JSON is snake_case (junction_id, desired_signals, ...).
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<TrafficService>();
builder.Services.AddHostedService<TrafficRuntimeService>();        // startup recovery + 500 ms ticker
builder.Services.AddHostedService<SimulatedControllerService>();   // answers commands with ACKs (when auto-ACK is on)

var app = builder.Build();

// Creates the database/tables on first run. For real migrations see README ("Database").
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TrafficDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();
app.UseDefaultFiles();   // serves wwwroot/index.html at "/"
app.UseStaticFiles();
app.MapControllers();

app.Run();
