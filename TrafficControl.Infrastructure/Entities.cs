using System.ComponentModel.DataAnnotations;

namespace TrafficControl.Infrastructure;

/// <summary>
/// One row per junction: its configuration and its latest state snapshot (both JSON).
/// Why a JSON snapshot instead of a table per concept? The engine state is ONE consistent unit
/// (queues + desired/actual signals + pending command + mode). Saving it in one row is atomic and trivial to recover.
/// </summary>
public sealed class JunctionEntity
{
    [MaxLength(32)] public string Id { get; set; } = "";
    [MaxLength(100)] public string Name { get; set; } = "";
    public string ConfigJson { get; set; } = "";
    public string StateJson { get; set; } = "";
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency: if a second app instance ever writes the same junction, SQL Server rejects the stale write.</summary>
    [Timestamp] public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>Append-only history: "why did the system reach its current state?"</summary>
public sealed class AuditLogEntity
{
    public long Id { get; set; }
    [MaxLength(32)] public string JunctionId { get; set; } = "";
    [MaxLength(50)] public string EventType { get; set; } = "";
    [MaxLength(10)] public string? Direction { get; set; }
    [MaxLength(50)] public string? PreviousState { get; set; }
    [MaxLength(50)] public string? NewState { get; set; }
    [MaxLength(50)] public string? CommandId { get; set; }
    [MaxLength(500)] public string Message { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
}
