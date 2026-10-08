using Microsoft.EntityFrameworkCore;
using TrafficControl.Application;
using TrafficControl.Domain;

namespace TrafficControl.Infrastructure;

/// <summary>
/// SQL Server implementation of the persistence port.
/// IDbContextFactory (not a scoped DbContext) because the actors are long-lived singletons, not HTTP requests.
/// One SaveChanges call = one database transaction, so snapshot + audit rows are atomic.
/// </summary>
public sealed class JunctionStore(IDbContextFactory<TrafficDbContext> factory) : IJunctionStore
{
    public async Task<IReadOnlyList<StoredJunction>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Junctions.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<StoredJunction?> GetAsync(string junctionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Junctions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == junctionId, ct);
        return row is null ? null : Map(row);
    }

    public async Task CreateAsync(JunctionConfig config, JunctionState state, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Junctions.Add(new JunctionEntity
        {
            Id = config.JunctionId,
            Name = config.Name,
            ConfigJson = SnapshotJson.Serialize(config),
            StateJson = SnapshotJson.Serialize(state),
            Version = state.Version,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveAsync(string junctionId, JunctionState state, IReadOnlyList<AuditEntry> audits, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Junctions.SingleAsync(x => x.Id == junctionId, ct);

        row.StateJson = SnapshotJson.Serialize(state);
        row.Version = state.Version;
        row.UpdatedAt = state.LastUpdated;

        db.AuditLog.AddRange(audits.Select(a => new AuditLogEntity
        {
            JunctionId = junctionId,
            EventType = a.EventType.ToString(),
            Direction = a.Direction?.ToString(),
            PreviousState = Trim(a.PreviousState, 50),
            NewState = Trim(a.NewState, 50),
            CommandId = Trim(a.CommandId, 50),
            Message = Trim(a.Message, 500) ?? "",
            Timestamp = a.Timestamp
        }));

        await db.SaveChangesAsync(ct);   // snapshot + audit rows commit together or not at all
    }

    public async Task<IReadOnlyList<AuditEntry>> GetHistoryAsync(string junctionId, int limit, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.AuditLog.AsNoTracking()
            .Where(x => x.JunctionId == junctionId)
            .OrderByDescending(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(r => new AuditEntry(
            r.JunctionId,
            Enum.TryParse<AuditEventType>(r.EventType, out var type) ? type : AuditEventType.ModeChanged,
            r.Message,
            r.Timestamp,
            Enum.TryParse<Direction>(r.Direction, out var dir) ? (Direction?)dir : null,
            r.PreviousState,
            r.NewState,
            r.CommandId)).ToList();
    }

    private static StoredJunction Map(JunctionEntity e) => new(
        SnapshotJson.Deserialize<JunctionConfig>(e.ConfigJson),
        SnapshotJson.Deserialize<JunctionState>(e.StateJson));

    private static string? Trim(string? value, int max) =>
        value is null ? null : (value.Length <= max ? value : value[..max]);
}
