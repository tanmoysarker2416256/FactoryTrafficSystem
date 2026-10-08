using TrafficControl.Domain;

namespace TrafficControl.Application;

/// <summary>Turns engine state into API read models. Always called inside the junction actor, so the view is consistent.</summary>
public static class StatusMapper
{
    public static JunctionStatusDto ToStatus(JunctionEngine engine, DateTimeOffset now)
    {
        var s = engine.State;
        var cfg = engine.Config;
        var dirs = cfg.AllDirections.ToList();

        SignalState Desired(Direction d) => s.Desired.GetValueOrDefault(d, SignalState.Unknown);
        SignalState Actual(Direction d) => s.Actual.GetValueOrDefault(d, SignalState.Unknown);
        List<QueuedVehicle> Queue(Direction d) => s.Queues.TryGetValue(d, out var q) ? q : new List<QueuedVehicle>();

        IReadOnlyList<VehicleDto> Vehicles(Direction d) => Queue(d)
            .OrderByDescending(v => cfg.Weight(v.Type))
            .ThenBy(v => v.ArrivedAt)
            .Take(25)
            .Select(v => new VehicleDto(v.VehicleId, Wire.Name(v.Type), Math.Max(0, Math.Round((now - v.ArrivedAt).TotalSeconds, 1))))
            .ToList();

        var pending = s.Pending is null
            ? null
            : new PendingCommandDto(
                s.Pending.CommandId, s.Pending.Purpose, s.Pending.Attempt, s.Pending.SentAt,
                s.Pending.Signals.ToDictionary(kv => Wire.Name(kv.Key), kv => Wire.Name(kv.Value)));

        var manual = s.Mode == OperatingMode.Manual && s.ManualPhaseId is not null && s.ManualExpiresAt is { } expires
            ? new ManualDto(s.ManualPhaseId, expires)
            : null;

        return new JunctionStatusDto(
            JunctionId: cfg.JunctionId,
            Name: cfg.Name,
            Mode: Wire.Name(s.Mode),
            Phase: s.Stage == SignalStage.AllRed ? "ALL_RED" : (s.ActivePhaseId ?? "NONE"),
            Stage: Wire.Name(s.Stage),
            TargetPhase: s.TargetPhaseId,
            ControllerStatus: Wire.Name(s.ControllerStatus),
            DesiredSignals: dirs.ToDictionary(d => Wire.Name(d), d => Wire.Name(Desired(d))),
            ActualSignals: dirs.ToDictionary(d => Wire.Name(d), d => Wire.Name(Actual(d))),
            Queues: dirs.ToDictionary(d => Wire.Name(d), d => Queue(d).Count),
            QueueVehicles: dirs.ToDictionary(d => Wire.Name(d), d => Vehicles(d)),
            DesiredActualMismatch: dirs.Any(d => Desired(d) != Actual(d)),
            PendingCommand: pending,
            Emergencies: s.Emergencies
                .OrderBy(e => e.DetectedAt)
                .Select(e => new EmergencyDto(e.VehicleId, Wire.Name(e.Direction), e.DetectedAt))
                .ToList(),
            Manual: manual,
            OfflineSensors: s.OfflineSensors.Select(d => Wire.Name(d)).OrderBy(x => x).ToList(),
            Alerts: s.Alerts
                .OrderBy(a => a.RaisedAt)
                .Select(a => new AlertDto(a.Code, a.Message, a.Direction is { } ad ? Wire.Name(ad) : null, a.RaisedAt))
                .ToList(),
            StageElapsedSeconds: Math.Max(0, Math.Round((now - s.StageEnteredAt).TotalSeconds, 1)),
            UpdatedAt: s.LastUpdated);
    }

    public static JunctionDetailDto ToDetail(JunctionEngine engine, DateTimeOffset now)
    {
        var cfg = engine.Config;
        return new JunctionDetailDto(
            cfg.JunctionId,
            cfg.Name,
            cfg.Phases.Select(p => new PhaseDto(p.Id, p.Directions.Select(d => Wire.Name(d)).ToList())).ToList(),
            new Dictionary<string, double>
            {
                ["yellow"] = cfg.Yellow.TotalSeconds,
                ["all_red"] = cfg.AllRed.TotalSeconds,
                ["min_green"] = cfg.MinGreen.TotalSeconds,
                ["green_duration"] = cfg.GreenDuration.TotalSeconds,
                ["max_green"] = cfg.MaxGreen.TotalSeconds,
                ["max_wait"] = cfg.MaxWait.TotalSeconds,
                ["manual_timeout"] = cfg.ManualTimeout.TotalSeconds,
                ["emergency_timeout"] = cfg.EmergencyTimeout.TotalSeconds,
                ["ack_timeout"] = cfg.AckTimeout.TotalSeconds
            },
            ToStatus(engine, now));
    }

    public static AuditEntryDto ToDto(AuditEntry e) => new(
        e.JunctionId, Wire.Name(e.EventType), e.Message,
        e.Direction is { } d ? Wire.Name(d) : null,
        e.PreviousState, e.NewState, e.CommandId, e.Timestamp);
}
