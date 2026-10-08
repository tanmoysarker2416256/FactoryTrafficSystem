namespace TrafficControl.Domain;

/// <summary>
/// Deterministic traffic-control engine for ONE junction.
///
/// Rules of the house:
///  1. No I/O, no clock, no threads, no sleeping. Time is always passed in as <c>now</c>.
///  2. Every public method is one "message". The caller must process messages for a junction one at a time
///     (the Application layer does this with a per-junction actor) - that is the consistency strategy.
///  3. Signals can only change inside the stage machine (StartYellow / StartAllRed / StartGreen).
///     Sensor events, manual commands and emergencies only change the TARGET PHASE, never the lamps.
///     That is why manual/emergency can never bypass the safe sequence.
///  4. GREEN is only issued after the controller has CONFIRMED all-red. "Sent" is never treated as "done".
/// </summary>
public sealed class JunctionEngine
{
    private const int MaxProcessedIds = 10_000;
    private const int MaxCommandHistory = 200;

    private readonly JunctionConfig _cfg;
    private readonly JunctionState _s;
    private readonly Func<string> _newId;
    private readonly HashSet<string> _processed;

    private List<DomainEffect> _fx = new();
    private DateTimeOffset _now;

    public JunctionEngine(JunctionConfig config, JunctionState state, Func<string>? idFactory = null)
    {
        config.Validate();
        _cfg = config;
        _s = state;
        _newId = idFactory ?? (() => "cmd-" + Guid.NewGuid().ToString("N")[..12]);
        _processed = new HashSet<string>(state.ProcessedEventIds);
    }

    public JunctionConfig Config => _cfg;
    public JunctionState State => _s;

    // =====================================================================================
    //  Public messages
    // =====================================================================================

    /// <summary>
    /// Call after creating the engine for a NEW junction and after EVERY application restart.
    /// We never trust what the lamps were showing before: actual state becomes Unknown, pending commands become
    /// Unknown, and we force ALL_RED and wait for the controller to confirm it before any GREEN is allowed.
    /// </summary>
    public EngineResult Start(DateTimeOffset now)
    {
        Begin(now);
        if (_s.Pending is not null)
        {
            SetHistory(_s.Pending.CommandId, CommandStatus.Unknown);
            _s.Pending = null;
        }
        foreach (var d in _cfg.AllDirections) _s.Actual[d] = SignalState.Unknown;

        _s.Stage = SignalStage.AllRed;
        _s.ActivePhaseId = null;
        _s.TargetPhaseId = null;
        _s.StageEnteredAt = now;
        _s.LastProbeAt = now;

        Audit(AuditEventType.Recovery,
            "Engine started: previous physical state is untrusted. Forcing ALL_RED and waiting for controller confirmation.");
        IssueCommand(AllSignals(SignalState.Red), "RECOVERY_ALL_RED");
        return Finish(Outcome.Accepted, "started");
    }

    /// <summary>Time passing. Driven by a background timer, never by sleeping inside a request.</summary>
    public EngineResult Tick(DateTimeOffset now)
    {
        Begin(now);
        ExpireManual();
        ExpireEmergencies();
        PruneTombstones();
        CheckPendingTimeout();
        ProbeIfDegraded();
        Advance();
        return Finish(Outcome.Accepted, "tick");
    }

    public EngineResult HandleSensorEvent(SensorEvent e, DateTimeOffset now)
    {
        Begin(now);

        if (string.IsNullOrWhiteSpace(e.EventId)) return Reject("event_id is required");
        if (string.IsNullOrWhiteSpace(e.VehicleId)) return Reject("vehicle_id is required");
        if (!Enum.IsDefined(e.Direction) || !_cfg.AllDirections.Contains(e.Direction)) return Reject("unknown direction for this junction");
        if (!Enum.IsDefined(e.EventType)) return Reject("unknown event_type");
        if (e.EventType == SensorEventType.VehicleArrived && (e.VehicleType is null || !Enum.IsDefined(e.VehicleType.Value)))
            return Reject("vehicle_type is required and must be known for VEHICLE_ARRIVED");

        // 1) Idempotency: event_id is the authority for "have I seen this exact event before?"
        if (_processed.Contains(e.EventId))
        {
            Audit(AuditEventType.DuplicateEventRejected, $"Duplicate event {e.EventId} ignored", e.Direction);
            return Finish(Outcome.Duplicate, "duplicate event ignored");
        }

        // 2) Freshness: sensor timestamp is only used to reject absurd events, never to order the queue.
        if (now - e.Timestamp > _cfg.MaxEventAge) return Reject($"event {e.EventId} is older than {_cfg.MaxEventAge.TotalMinutes:0} min (dead-lettered)");
        if (e.Timestamp - now > _cfg.MaxFutureSkew) return Reject($"event {e.EventId} timestamp is too far in the future (sensor clock skew)");

        MarkProcessed(e.EventId);
        NoteSequence(e);

        var message = e.EventType == SensorEventType.VehicleArrived ? OnArrived(e) : OnCleared(e);
        Advance();
        return Finish(Outcome.Accepted, message);
    }

    public EngineResult HandleAdminCommand(AdminCommand cmd, DateTimeOffset now)
    {
        Begin(now);
        switch (cmd.Type)
        {
            case AdminCommandType.ManualGreenRequest: return ManualGreen(cmd);
            case AdminCommandType.ReturnToAutomatic: return ReturnToAutomatic(cmd);
            default:
                Audit(AuditEventType.CommandRejected, $"Unknown admin command {cmd.Type}");
                return Finish(Outcome.Rejected, "unknown command");
        }
    }

    public EngineResult HandleControllerAck(ControllerAck ack, DateTimeOffset now)
    {
        Begin(now);
        if (!string.Equals(ack.JunctionId, _cfg.JunctionId, StringComparison.OrdinalIgnoreCase))
            return Finish(Outcome.Rejected, "junction_id does not match");

        if (string.IsNullOrWhiteSpace(ack.CommandId) || !_s.CommandHistory.TryGetValue(ack.CommandId, out var known))
        {
            Audit(AuditEventType.AckIgnored, $"ACK for unknown command '{ack.CommandId}' ignored");
            return Finish(Outcome.Rejected, "unknown command_id");
        }

        var cmd = _s.Pending;
        if (cmd is null || cmd.CommandId != ack.CommandId)
        {
            // Duplicate ACK, late ACK for a superseded command, or ACK after timeout: never re-apply.
            Audit(AuditEventType.AckIgnored, $"ACK for {ack.CommandId} ignored: command is already {known}", null, null, null, ack.CommandId);
            return Finish(Outcome.Duplicate, $"command already {known}");
        }

        Audit(AuditEventType.ControllerAck, $"Controller answered {ack.Status} for {ack.CommandId}", null, null, ack.Status.ToString(), ack.CommandId);
        _s.ControllerStatus = DeviceStatus.Online;   // any valid answer proves the controller is reachable

        if (ack.Status != AckStatus.Ack)
        {
            _s.Pending = null;
            SetHistory(cmd.CommandId, CommandStatus.Failed);
            HandleCommandFailure(cmd, $"controller answered {ack.Status}");
            Advance();
            return Finish(Outcome.Accepted, "command failure handled");
        }

        // Record what the controller says is REALLY showing.
        foreach (var d in _cfg.AllDirections)
        {
            var confirmed = ack.ActualSignals is null
                ? cmd.Signals[d]
                : ack.ActualSignals.GetValueOrDefault(d, SignalState.Unknown);
            var previous = _s.Actual.GetValueOrDefault(d, SignalState.Unknown);
            _s.Actual[d] = confirmed;
            if (previous != confirmed)
                Audit(AuditEventType.SignalConfirmed, $"Confirmed {d}: {previous} -> {confirmed}", d, previous.ToString(), confirmed.ToString(), cmd.CommandId);
        }
        _s.Pending = null;
        SetHistory(cmd.CommandId, CommandStatus.Acknowledged);
        ClearAlert(AlertCodes.CommandTimeout);

        if (ActualConflicts())
        {
            RaiseAlert(AlertCodes.UnsafeActual, "Controller reports conflicting physical signals");
            EnterDegraded("controller reports conflicting physical signals");
        }
        else if (_cfg.AllDirections.Any(d => _s.Actual[d] != cmd.Signals[d]))
        {
            RaiseAlert(AlertCodes.StateMismatch, $"Desired and actual signal state differ after {cmd.CommandId}");
            HandleCommandFailure(cmd, "actual state differs from desired state");
        }
        else
        {
            ClearAlert(AlertCodes.StateMismatch);
            TryExitDegraded();
        }

        Advance();
        return Finish(Outcome.Accepted, "ack applied");
    }

    public EngineResult HandleDeviceStatus(DeviceStatusEvent e, DateTimeOffset now)
    {
        Begin(now);
        if (string.IsNullOrWhiteSpace(e.EventId)) return Reject("event_id is required");
        if (e.DeviceType == DeviceType.Sensor && (e.Direction is null || !_cfg.AllDirections.Contains(e.Direction.Value)))
            return Reject("direction is required for sensor status");
        if (e.DeviceType == DeviceType.Signal && (e.Direction is null || !_cfg.AllDirections.Contains(e.Direction.Value)))
            return Reject("direction is required for signal status");

        if (_processed.Contains(e.EventId))
        {
            Audit(AuditEventType.DuplicateEventRejected, $"Duplicate status event {e.EventId} ignored");
            return Finish(Outcome.Duplicate, "duplicate event ignored");
        }
        MarkProcessed(e.EventId);

        var failed = e.Status is DeviceStatus.Offline or DeviceStatus.Degraded or DeviceStatus.Unknown;
        switch (e.DeviceType)
        {
            case DeviceType.Sensor:
                if (failed)
                {
                    _s.OfflineSensors.Add(e.Direction!.Value);
                    RaiseAlert(AlertCodes.SensorOffline, $"Sensor {e.Direction} is {e.Status}; assuming demand (fixed-time fallback)", e.Direction);
                    Audit(AuditEventType.DeviceFailure, $"Sensor {e.Direction} is {e.Status}", e.Direction);
                }
                else if (e.Status == DeviceStatus.Online)
                {
                    _s.OfflineSensors.Remove(e.Direction!.Value);
                    ClearAlert(AlertCodes.SensorOffline, e.Direction);
                    Audit(AuditEventType.DeviceRecovered, $"Sensor {e.Direction} is back online", e.Direction);
                }
                break;

            case DeviceType.SignalController:
                _s.ControllerStatus = e.Status;
                if (failed)
                {
                    RaiseAlert(AlertCodes.ControllerOffline, $"Controller is {e.Status}");
                    Audit(AuditEventType.DeviceFailure, $"Controller is {e.Status}");
                    EnterDegraded($"controller is {e.Status}");
                }
                else if (e.Status == DeviceStatus.Online)
                {
                    ClearAlert(AlertCodes.ControllerOffline);
                    Audit(AuditEventType.DeviceRecovered, "Controller reported ONLINE; requesting ALL_RED confirmation before resuming");
                    if (_s.Mode == OperatingMode.Degraded && _s.Pending is null)
                    {
                        _s.LastProbeAt = _now;
                        IssueCommand(AllSignals(SignalState.Red), "RECOVERY_ALL_RED");
                    }
                }
                else
                {
                    RaiseAlert(AlertCodes.ControllerOffline, $"Controller status: {e.Status}");
                }
                break;

            case DeviceType.Signal:
                if (failed)
                {
                    RaiseAlert(AlertCodes.SignalFailure, $"Signal {e.Direction} is {e.Status}", e.Direction);
                    Audit(AuditEventType.DeviceFailure, $"Signal {e.Direction} is {e.Status}", e.Direction);
                    EnterDegraded($"signal {e.Direction} is {e.Status}");
                }
                else if (e.Status == DeviceStatus.Online)
                {
                    ClearAlert(AlertCodes.SignalFailure, e.Direction);
                    Audit(AuditEventType.DeviceRecovered, $"Signal {e.Direction} is back online", e.Direction);
                }
                break;
        }

        Advance();
        return Finish(Outcome.Accepted, "status applied");
    }

    // =====================================================================================
    //  Stage machine: the ONLY place signals change
    // =====================================================================================

    private void Advance()
    {
        if (_s.Mode == OperatingMode.Degraded) return;   // fail-safe: hold; recovery has its own path

        switch (_s.Stage)
        {
            case SignalStage.Green:
                {
                    var target = PriorityPhase() ?? AutomaticSwitchTarget();
                    if (target is not null && target != _s.ActivePhaseId) StartYellow(target);
                    break;
                }
            case SignalStage.Yellow:
                if (_now - _s.StageEnteredAt >= _cfg.Yellow) StartAllRed();
                break;

            case SignalStage.AllRed:
                // Two gates: minimum clearance time AND controller-confirmed all-red.
                if (_now - _s.StageEnteredAt >= _cfg.AllRed && AllRedConfirmed())
                    StartGreen(PriorityPhase() ?? _s.TargetPhaseId ?? BestPhase());
                break;
        }
    }

    private void StartYellow(string target)
    {
        var active = _s.ActivePhaseId!;
        _s.Stage = SignalStage.Yellow;
        _s.TargetPhaseId = target;
        _s.StageEnteredAt = _now;
        Audit(AuditEventType.TransitionStarted, $"Transition {active} -> {target}: YELLOW on {active} (mode {_s.Mode})");
        IssueCommand(SignalsFor(active, SignalState.Yellow), "YELLOW");
    }

    private void StartAllRed()
    {
        _s.Stage = SignalStage.AllRed;
        _s.StageEnteredAt = _now;
        Audit(AuditEventType.TransitionStarted, $"ALL_RED clearance before {_s.TargetPhaseId}");
        IssueCommand(AllSignals(SignalState.Red), "ALL_RED");
    }

    private void StartGreen(string target)
    {
        if (!AllRedConfirmed())
            throw new SafetyViolationException("Attempted GREEN without controller-confirmed ALL_RED.");

        _s.Stage = SignalStage.Green;
        _s.ActivePhaseId = target;
        _s.TargetPhaseId = null;
        _s.StageEnteredAt = _now;
        Audit(AuditEventType.TransitionStarted, $"GREEN on {target}");
        IssueCommand(SignalsFor(target, SignalState.Green), "GREEN");
    }

    private bool AllRedConfirmed() =>
        _s.Pending is null && _cfg.AllDirections.All(d => _s.Actual.GetValueOrDefault(d, SignalState.Unknown) == SignalState.Red);

    // =====================================================================================
    //  Scheduling (what phase should be green?)
    // =====================================================================================

    /// <summary>Emergency and manual mode name a phase. Automatic mode returns null and the scorer decides.</summary>
    private string? PriorityPhase() => _s.Mode switch
    {
        OperatingMode.Emergency => EmergencyPhaseId(),
        OperatingMode.Manual => _s.ManualPhaseId,
        _ => null
    };

    /// <summary>Competing emergencies: first come, first served (server time). Others wait their turn.</summary>
    private string? EmergencyPhaseId()
    {
        var first = _s.Emergencies.OrderBy(x => x.DetectedAt).ThenBy(x => x.VehicleId, StringComparer.Ordinal).FirstOrDefault();
        return first is null ? null : _cfg.PhaseOf(first.Direction).Id;
    }

    /// <summary>Returns the phase to switch to, or null to keep the current green.</summary>
    private string? AutomaticSwitchTarget()
    {
        var active = _cfg.GetPhase(_s.ActivePhaseId!);
        var elapsed = _now - _s.StageEnteredAt;
        if (elapsed < _cfg.MinGreen) return null;                       // avoid flip-flopping

        var others = _cfg.Phases.Where(p => p.Id != active.Id).ToList();

        // Starvation guard: somebody has waited too long, they go next no matter the scores.
        var starved = others
            .Where(p => OldestWaitSeconds(p) >= _cfg.MaxWait.TotalSeconds)
            .OrderByDescending(p => OldestWaitSeconds(p))
            .FirstOrDefault();
        if (starved is not null) return starved.Id;

        PhaseDefinition? best = null;
        double bestScore = 0;
        foreach (var p in others)
        {
            var score = Score(p);
            if (score > bestScore) { best = p; bestScore = score; }
        }
        if (best is null) return null;                                  // nobody else is waiting: do not switch needlessly
        if (WaitingCount(active) == 0) return best.Id;                  // green would be wasted
        if (elapsed >= _cfg.MaxGreen) return best.Id;                   // hard cap on extension

        var currentScore = Score(active);
        if (elapsed < _cfg.GreenDuration)
            return bestScore > currentScore * _cfg.SwitchThreshold ? best.Id : null;   // early switch only if clearly worth it
        return currentScore > bestScore * _cfg.SwitchThreshold ? null : best.Id;       // extend only if current is clearly heavier
    }

    /// <summary>Used when no phase is active yet (startup/recovery).</summary>
    private string BestPhase()
    {
        var starved = _cfg.Phases.Where(p => OldestWaitSeconds(p) >= _cfg.MaxWait.TotalSeconds)
            .OrderByDescending(p => OldestWaitSeconds(p)).FirstOrDefault();
        if (starved is not null) return starved.Id;

        PhaseDefinition? best = null;
        double bestScore = 0;
        foreach (var p in _cfg.Phases)
        {
            var score = Score(p);
            if (score > bestScore) { best = p; bestScore = score; }
        }
        return best?.Id ?? _cfg.Phases[0].Id;
    }

    /// <summary>Score = sum over waiting vehicles of (type weight + waiting-time bonus).</summary>
    private double Score(PhaseDefinition p)
    {
        double total = 0;
        foreach (var d in p.Directions)
        {
            foreach (var v in Q(d))
            {
                var wait = Math.Max(0, (_now - v.ArrivedAt).TotalSeconds);
                total += _cfg.Weight(v.Type) + wait * _cfg.WaitBonusPerSecond;
            }
            if (_s.OfflineSensors.Contains(d)) total += _cfg.AssumedDemandWhenSensorOffline;
        }
        return total;
    }

    private int WaitingCount(PhaseDefinition p) =>
        p.Directions.Sum(d => Q(d).Count + (_s.OfflineSensors.Contains(d) ? 1 : 0));

    private double OldestWaitSeconds(PhaseDefinition p)
    {
        double oldest = 0;
        foreach (var d in p.Directions)
            foreach (var v in Q(d))
                oldest = Math.Max(oldest, (_now - v.ArrivedAt).TotalSeconds);
        return oldest;
    }

    // =====================================================================================
    //  Vehicles and queues
    // =====================================================================================

    private string OnArrived(SensorEvent e)
    {
        // Queue stores vehicle IDs, not a counter. So repeats are harmless and the count can never go negative.
        var arrivedAt = e.Timestamp > _now ? _now : e.Timestamp;   // clamp small clock skew

        if (_s.ClearedTombstones.Remove(e.VehicleId))
        {
            Audit(AuditEventType.VehicleDetected, $"Late arrival of {e.VehicleId} ignored: its CLEARED event was already received", e.Direction);
            return "late arrival ignored (already cleared)";
        }

        var existing = FindVehicle(e.VehicleId);
        if (existing is not null)
        {
            if (existing.Value.Dir == e.Direction)
            {
                RefreshEmergency(e.VehicleId);
                Audit(AuditEventType.VehicleDetected, $"{e.VehicleId} already queued on {e.Direction}; no change", e.Direction);
                return "already queued";
            }
            // A vehicle cannot be in two queues: the latest event wins.
            Q(existing.Value.Dir).Remove(existing.Value.Vehicle);
            Audit(AuditEventType.SequenceAnomaly, $"{e.VehicleId} moved from {existing.Value.Dir} to {e.Direction}", e.Direction);
        }

        Q(e.Direction).Add(new QueuedVehicle { VehicleId = e.VehicleId, Type = e.VehicleType!.Value, ArrivedAt = arrivedAt });
        Audit(AuditEventType.VehicleDetected, $"{e.VehicleType} {e.VehicleId} arrived on {e.Direction}", e.Direction);

        if (e.VehicleType == VehicleType.Emergency) OnEmergencyDetected(e.VehicleId, e.Direction);
        return "vehicle queued";
    }

    private string OnCleared(SensorEvent e)
    {
        var found = FindVehicle(e.VehicleId);
        if (found is null)
        {
            // CLEARED before ARRIVED (out of order) or never arrived. Never go below zero; remember it so a late arrival is ignored.
            _s.ClearedTombstones[e.VehicleId] = _now;
            Audit(AuditEventType.VehicleCleared, $"CLEARED for unknown vehicle {e.VehicleId}: recorded as tombstone, queue unchanged", e.Direction);
            return "orphan clear recorded";
        }

        Q(found.Value.Dir).Remove(found.Value.Vehicle);
        if (found.Value.Dir != e.Direction)
            Audit(AuditEventType.SequenceAnomaly, $"{e.VehicleId} cleared on {e.Direction} but was queued on {found.Value.Dir}", e.Direction);
        Audit(AuditEventType.VehicleCleared, $"{e.VehicleId} cleared from {found.Value.Dir}", found.Value.Dir);

        if (_s.Emergencies.Any(x => x.VehicleId == e.VehicleId)) ClearEmergency(e.VehicleId, "emergency vehicle cleared the junction");
        return "vehicle cleared";
    }

    private void NoteSequence(SensorEvent e)
    {
        if (_s.LastSequence.TryGetValue(e.Direction, out var last) && e.SequenceNo <= last)
            Audit(AuditEventType.SequenceAnomaly,
                $"Out-of-order or replayed sequence {e.SequenceNo} (last {last}) on {e.Direction}; applied because queue updates are idempotent per vehicle", e.Direction);
        else
            _s.LastSequence[e.Direction] = e.SequenceNo;
    }

    private (Direction Dir, QueuedVehicle Vehicle)? FindVehicle(string vehicleId)
    {
        foreach (var (dir, list) in _s.Queues)
        {
            var v = list.FirstOrDefault(x => x.VehicleId == vehicleId);
            if (v is not null) return (dir, v);
        }
        return null;
    }

    private List<QueuedVehicle> Q(Direction d)
    {
        if (!_s.Queues.TryGetValue(d, out var list)) _s.Queues[d] = list = new List<QueuedVehicle>();
        return list;
    }

    private void PruneTombstones()
    {
        var expired = _s.ClearedTombstones.Where(kv => _now - kv.Value > _cfg.MaxEventAge).Select(kv => kv.Key).ToList();
        foreach (var id in expired) _s.ClearedTombstones.Remove(id);
    }

    // =====================================================================================
    //  Emergency
    // =====================================================================================

    private void OnEmergencyDetected(string vehicleId, Direction direction)
    {
        _s.Emergencies.Add(new EmergencyRequest { VehicleId = vehicleId, Direction = direction, DetectedAt = _now, LastSeenAt = _now });
        Audit(AuditEventType.EmergencyDetected, $"Emergency vehicle {vehicleId} detected on {direction}", direction);

        if (_s.Mode == OperatingMode.Degraded)
        {
            Audit(AuditEventType.EmergencyDetected, "Cannot preempt: junction is DEGRADED (controller not trusted). Emergency is recorded.", direction);
            return;
        }
        if (_s.Mode == OperatingMode.Manual)
        {
            // Policy: emergency overrides manual. Manual is cancelled, the admin must re-issue it afterwards.
            Audit(AuditEventType.ManualOverride, "Manual override cancelled by emergency");
            _s.ManualPhaseId = null;
            _s.ManualExpiresAt = null;
        }
        SetMode(OperatingMode.Emergency, $"emergency vehicle {vehicleId} on {direction}");
    }

    private void RefreshEmergency(string vehicleId)
    {
        var req = _s.Emergencies.FirstOrDefault(x => x.VehicleId == vehicleId);
        if (req is not null) req.LastSeenAt = _now;   // repeated sightings keep the emergency alive
    }

    private void ClearEmergency(string vehicleId, string reason)
    {
        var removed = _s.Emergencies.RemoveAll(x => x.VehicleId == vehicleId);
        if (removed == 0) return;
        Audit(AuditEventType.EmergencyCleared, $"Emergency {vehicleId} cleared: {reason}");
        if (_s.Emergencies.Count == 0 && _s.Mode == OperatingMode.Emergency)
            SetMode(OperatingMode.Automatic, "no active emergencies");
    }

    private void ExpireEmergencies()
    {
        var stale = _s.Emergencies.Where(x => _now - x.LastSeenAt >= _cfg.EmergencyTimeout).ToList();
        foreach (var req in stale)
        {
            // Stale emergency: also drop its queue entry, otherwise a phantom weight-100 vehicle would distort scheduling forever.
            var found = FindVehicle(req.VehicleId);
            if (found is not null) Q(found.Value.Dir).Remove(found.Value.Vehicle);
            ClearEmergency(req.VehicleId, $"timed out after {_cfg.EmergencyTimeout.TotalSeconds:0}s without update (stale)");
        }
    }

    // =====================================================================================
    //  Manual control
    // =====================================================================================

    private EngineResult ManualGreen(AdminCommand cmd)
    {
        if (cmd.Direction is null || !Enum.IsDefined(cmd.Direction.Value) || !_cfg.AllDirections.Contains(cmd.Direction.Value))
            return Deny(Outcome.Rejected, "direction is required and must belong to this junction");
        if (_s.Mode == OperatingMode.Emergency)
            return Deny(Outcome.Conflict, "emergency mode is active; manual control is not allowed until it clears");
        if (_s.Mode == OperatingMode.Degraded)
            return Deny(Outcome.Conflict, "junction is degraded; manual control is unavailable until the controller is confirmed healthy");

        var phase = _cfg.PhaseOf(cmd.Direction.Value).Id;
        var replaced = _s.Mode == OperatingMode.Manual ? _s.ManualPhaseId : null;   // two admins: last command wins (serialised)

        _s.ManualPhaseId = phase;
        _s.ManualExpiresAt = _now + _cfg.ManualTimeout;
        SetMode(OperatingMode.Manual, $"manual green requested for {cmd.Direction}");
        Audit(AuditEventType.ManualOverride,
            $"Manual green requested for {cmd.Direction} (phase {phase}) by {cmd.RequestedBy ?? "unknown"}" +
            (replaced is null ? "" : $"; replaces earlier manual request for {replaced}") +
            $"; expires {_s.ManualExpiresAt:O}", cmd.Direction);

        Advance();
        return Finish(Outcome.Accepted, "manual request accepted; safe transition will be executed");
    }

    private EngineResult ReturnToAutomatic(AdminCommand cmd)
    {
        switch (_s.Mode)
        {
            case OperatingMode.Automatic:
                return Finish(Outcome.Accepted, "already automatic");
            case OperatingMode.Manual:
                _s.ManualPhaseId = null;
                _s.ManualExpiresAt = null;
                SetMode(OperatingMode.Automatic, "administrator returned control to automatic");
                Audit(AuditEventType.ReturnToAutomatic, $"Returned to automatic by {cmd.RequestedBy ?? "unknown"}");
                Advance();
                return Finish(Outcome.Accepted, "returned to automatic");
            case OperatingMode.Emergency:
                return Deny(Outcome.Conflict, "emergency mode is active; it ends when the emergency clears");
            default:
                return Deny(Outcome.Conflict, "junction is degraded; it recovers when the controller confirms ALL_RED");
        }
    }

    private void ExpireManual()
    {
        if (_s.Mode == OperatingMode.Manual && _s.ManualExpiresAt is { } exp && _now >= exp)
        {
            _s.ManualPhaseId = null;
            _s.ManualExpiresAt = null;
            Audit(AuditEventType.ManualExpired, "Manual override expired");
            SetMode(OperatingMode.Automatic, "manual override expired (also covers a disconnected administrator)");
        }
    }

    private EngineResult Deny(Outcome outcome, string reason)
    {
        Audit(AuditEventType.CommandRejected, reason);
        return Finish(outcome, reason);
    }

    // =====================================================================================
    //  Controller commands, timeouts, degraded mode
    // =====================================================================================

    private void IssueCommand(Dictionary<Direction, SignalState> signals, string purpose)
    {
        if (_s.Pending is not null) SetHistory(_s.Pending.CommandId, CommandStatus.Superseded);

        var rec = new CommandRecord
        {
            CommandId = _newId(),
            Signals = new Dictionary<Direction, SignalState>(signals),
            Attempt = 1,
            SentAt = _now,
            Purpose = purpose
        };
        _s.Pending = rec;
        SetHistory(rec.CommandId, CommandStatus.Pending);
        SetDesired(signals, rec.CommandId);
        Emit(new SendControllerCommand(rec.CommandId, _cfg.JunctionId, new Dictionary<Direction, SignalState>(rec.Signals), 1));
    }

    private void SetDesired(Dictionary<Direction, SignalState> signals, string? commandId)
    {
        foreach (var d in _cfg.AllDirections)
        {
            var previous = _s.Desired.GetValueOrDefault(d, SignalState.Unknown);
            var next = signals[d];
            if (previous != next)
                Audit(AuditEventType.SignalRequested, $"Desired {d}: {previous} -> {next}", d, previous.ToString(), next.ToString(), commandId);
        }
        _s.Desired = new Dictionary<Direction, SignalState>(signals);
    }

    private void CheckPendingTimeout()
    {
        var p = _s.Pending;
        if (p is null || _now - p.SentAt < _cfg.AckTimeout) return;
        HandleCommandFailure(p, $"no ACK within {_cfg.AckTimeout.TotalSeconds:0}s");
    }

    /// <summary>Retry with the SAME command_id (so a late first ACK still correlates), then fall back to safe degraded mode.</summary>
    private void HandleCommandFailure(CommandRecord cmd, string reason)
    {
        if (_s.Mode != OperatingMode.Degraded && cmd.Attempt <= _cfg.MaxAckRetries)
        {
            cmd.Attempt++;
            cmd.SentAt = _now;
            _s.Pending = cmd;
            SetHistory(cmd.CommandId, CommandStatus.Pending);
            Audit(AuditEventType.ControllerTimeout, $"{reason}; retrying {cmd.CommandId} (attempt {cmd.Attempt})", null, null, null, cmd.CommandId);
            Emit(new SendControllerCommand(cmd.CommandId, _cfg.JunctionId, new Dictionary<Direction, SignalState>(cmd.Signals), cmd.Attempt));
            return;
        }

        if (_s.Pending?.CommandId == cmd.CommandId) _s.Pending = null;
        SetHistory(cmd.CommandId, CommandStatus.TimedOut);
        RaiseAlert(AlertCodes.CommandTimeout, $"Command {cmd.CommandId} ({cmd.Purpose}) was never confirmed: {reason}");
        Audit(AuditEventType.ControllerTimeout, $"Giving up on {cmd.CommandId}: {reason}", null, null, null, cmd.CommandId);
        EnterDegraded(reason);
    }

    /// <summary>
    /// Safety first: when we cannot trust the controller we stop granting GREEN, ask for ALL_RED, and mark the physical
    /// state Unknown. We never assume an unconfirmed command was executed.
    /// </summary>
    private void EnterDegraded(string reason)
    {
        if (_s.Mode == OperatingMode.Degraded) return;

        if (_s.Pending is not null)
        {
            SetHistory(_s.Pending.CommandId, CommandStatus.TimedOut);
            _s.Pending = null;
        }
        _s.ManualPhaseId = null;
        _s.ManualExpiresAt = null;
        SetMode(OperatingMode.Degraded, reason);
        RaiseAlert(AlertCodes.Degraded, $"Junction degraded: {reason}");
        foreach (var d in _cfg.AllDirections) _s.Actual[d] = SignalState.Unknown;
        _s.LastProbeAt = _now;

        if (_s.ControllerStatus == DeviceStatus.Offline)
            SetDesired(AllSignals(SignalState.Red), null);           // nobody to talk to: record the intent only
        else
            IssueCommand(AllSignals(SignalState.Red), "FAILSAFE_ALL_RED");
    }

    private void ProbeIfDegraded()
    {
        if (_s.Mode != OperatingMode.Degraded || _s.Pending is not null || _s.ControllerStatus == DeviceStatus.Offline) return;
        if (_now - _s.LastProbeAt < _cfg.DegradedProbeInterval) return;
        _s.LastProbeAt = _now;
        IssueCommand(AllSignals(SignalState.Red), "PROBE_ALL_RED");
    }

    private void TryExitDegraded()
    {
        if (_s.Mode != OperatingMode.Degraded) return;
        if (!AllRedConfirmed()) return;
        if (_s.Alerts.Any(a => a.Code == AlertCodes.SignalFailure)) return;

        ClearAlert(AlertCodes.Degraded);
        ClearAlert(AlertCodes.CommandTimeout);
        ClearAlert(AlertCodes.StateMismatch);
        ClearAlert(AlertCodes.UnsafeActual);
        ClearAlert(AlertCodes.ControllerOffline);

        _s.Stage = SignalStage.AllRed;
        _s.ActivePhaseId = null;
        _s.TargetPhaseId = null;
        _s.StageEnteredAt = _now;
        SetMode(_s.Emergencies.Count > 0 ? OperatingMode.Emergency : OperatingMode.Automatic, "controller recovered and confirmed ALL_RED");
    }

    // =====================================================================================
    //  Safety checks
    // =====================================================================================

    private void AssertDesiredSafe()
    {
        var greens = _cfg.Phases.Where(p => p.Directions.Any(d => _s.Desired.GetValueOrDefault(d, SignalState.Unknown) == SignalState.Green)).ToList();
        if (greens.Count > 1)
            throw new SafetyViolationException($"Conflicting GREEN phases desired: {string.Join(", ", greens.Select(g => g.Id))}");
        if (greens.Count == 1)
        {
            var offenders = _cfg.AllDirections.Except(greens[0].Directions)
                .Where(d => _s.Desired.GetValueOrDefault(d, SignalState.Unknown) != SignalState.Red).ToList();
            if (offenders.Count > 0)
                throw new SafetyViolationException($"GREEN on {greens[0].Id} while {string.Join(", ", offenders)} are not RED");
        }
    }

    /// <summary>The controller is external input, so a conflicting report degrades the junction instead of throwing.</summary>
    private bool ActualConflicts()
    {
        var greens = _cfg.Phases.Where(p => p.Directions.Any(d => _s.Actual.GetValueOrDefault(d, SignalState.Unknown) == SignalState.Green)).ToList();
        if (greens.Count > 1) return true;
        if (greens.Count == 1)
            return _cfg.AllDirections.Except(greens[0].Directions)
                .Any(d => _s.Actual.GetValueOrDefault(d, SignalState.Unknown) is SignalState.Green or SignalState.Yellow);
        return false;
    }

    // =====================================================================================
    //  Small helpers
    // =====================================================================================

    private Dictionary<Direction, SignalState> AllSignals(SignalState state) =>
        _cfg.AllDirections.ToDictionary(d => d, _ => state);

    private Dictionary<Direction, SignalState> SignalsFor(string phaseId, SignalState state)
    {
        var phase = _cfg.GetPhase(phaseId);
        return _cfg.AllDirections.ToDictionary(d => d, d => phase.Directions.Contains(d) ? state : SignalState.Red);
    }

    private void SetMode(OperatingMode mode, string reason)
    {
        if (_s.Mode == mode) return;
        var previous = _s.Mode;
        _s.Mode = mode;
        Audit(AuditEventType.ModeChanged, $"Mode {previous} -> {mode}: {reason}", null, previous.ToString(), mode.ToString());
    }

    private void RaiseAlert(string code, string message, Direction? direction = null)
    {
        _s.Alerts.RemoveAll(a => a.Code == code && a.Direction == direction);
        _s.Alerts.Add(new Alert { Code = code, Message = message, Direction = direction, RaisedAt = _now });
    }

    private void ClearAlert(string code, Direction? direction = null) =>
        _s.Alerts.RemoveAll(a => a.Code == code && a.Direction == direction);

    private void SetHistory(string commandId, CommandStatus status)
    {
        if (!_s.CommandHistory.ContainsKey(commandId)) _s.CommandOrder.Add(commandId);
        _s.CommandHistory[commandId] = status;
        while (_s.CommandOrder.Count > MaxCommandHistory)
        {
            _s.CommandHistory.Remove(_s.CommandOrder[0]);
            _s.CommandOrder.RemoveAt(0);
        }
    }

    private void MarkProcessed(string eventId)
    {
        if (!_processed.Add(eventId)) return;
        _s.ProcessedEventIds.Add(eventId);
        while (_s.ProcessedEventIds.Count > MaxProcessedIds)
        {
            _processed.Remove(_s.ProcessedEventIds[0]);
            _s.ProcessedEventIds.RemoveAt(0);
        }
    }

    private void Begin(DateTimeOffset now)
    {
        _now = now;
        _fx = new List<DomainEffect>();
    }

    private EngineResult Reject(string reason)
    {
        Audit(AuditEventType.EventRejected, reason);
        return Finish(Outcome.Rejected, reason);
    }

    private EngineResult Finish(Outcome outcome, string message)
    {
        _s.Version++;
        _s.LastUpdated = _now;
        AssertDesiredSafe();   // last line of defence: the engine refuses to return an unsafe desired state
        return new EngineResult(outcome, message, _fx);
    }

    private void Emit(DomainEffect effect) => _fx.Add(effect);

    private void Audit(AuditEventType type, string message, Direction? direction = null,
        string? previous = null, string? next = null, string? commandId = null) =>
        Emit(new WriteAudit(new AuditEntry(_cfg.JunctionId, type, message, _now, direction, previous, next, commandId)));
}
