using TrafficControl.Domain;

namespace TrafficControl.Application;

/// <summary>Time is a dependency, never DateTime.UtcNow inside logic. Tests and the engine stay deterministic.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// PORT to the physical world. Today the adapter is a REST-driven simulator; tomorrow an MQTT adapter
/// implements the same interface and the engine/application code does not change.
/// </summary>
public interface IControllerGateway
{
    Task SendAsync(SendControllerCommand command, CancellationToken ct = default);
}

public sealed record StoredJunction(JunctionConfig Config, JunctionState State);

/// <summary>
/// PORT to persistence. SaveAsync stores the state snapshot and the audit rows in ONE transaction,
/// so "what we decided" and "why" can never disagree after a crash.
/// </summary>
public interface IJunctionStore
{
    Task<IReadOnlyList<StoredJunction>> ListAsync(CancellationToken ct = default);
    Task<StoredJunction?> GetAsync(string junctionId, CancellationToken ct = default);
    Task CreateAsync(JunctionConfig config, JunctionState state, CancellationToken ct = default);
    Task SaveAsync(string junctionId, JunctionState state, IReadOnlyList<AuditEntry> audits, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetHistoryAsync(string junctionId, int limit, CancellationToken ct = default);
}
