using TrafficControl.Domain;
using System.Text.Json;

namespace TrafficControl.Domain.Tests;

public class EngineTests
{
    private static readonly TimeSpan Half = TimeSpan.FromMilliseconds(500);

    private static bool IsNorthSouth(Direction d) => d is Direction.North or Direction.South;

    // ------------------------------------------------------------------ startup & safety

    [Fact]
    public void Startup_forces_all_red_and_only_then_serves_a_phase()
    {
        var sim = new Sim();
        Assert.All(sim.Sent[0].Signals.Values, s => Assert.Equal(SignalState.Red, s));

        sim.Advance(4);
        Assert.Equal(SignalState.Green, sim.Desired(Direction.North));
        Assert.Equal(SignalState.Red, sim.Desired(Direction.East));
    }

    [Fact]
    public void Green_is_never_requested_while_the_controller_does_not_confirm_all_red()
    {
        var sim = new Sim(autoAck: false);
        sim.Advance(40);

        Assert.DoesNotContain(sim.Sent, c => c.Signals.Values.Contains(SignalState.Green));
        Assert.Equal(OperatingMode.Degraded, sim.State.Mode);
        Assert.Contains(sim.State.Alerts, a => a.Code == AlertCodes.CommandTimeout);
        Assert.All(sim.State.Actual.Values, s => Assert.Equal(SignalState.Unknown, s));   // never assume it worked
    }

    [Fact]
    public void Random_event_storm_never_produces_an_unsafe_command_sequence()
    {
        var rng = new Random(42);
        var sim = new Sim();
        var dirs = Enum.GetValues<Direction>();
        var vehicles = new List<(Direction Dir, string Id)>();
        var n = 0;

        for (var i = 0; i < 4000; i++)
        {
            switch (rng.Next(12))
            {
                case 0:
                case 1:
                case 2:
                    {
                        var d = dirs[rng.Next(4)];
                        var t = rng.Next(10) == 0 ? VehicleType.Emergency : (VehicleType)rng.Next(3);
                        var id = $"V{++n}";
                        sim.Arrive(d, t, id);
                        vehicles.Add((d, id));
                        break;
                    }
                case 3:
                case 4:
                    if (vehicles.Count > 0)
                    {
                        var k = rng.Next(vehicles.Count);
                        sim.Clear(vehicles[k].Dir, vehicles[k].Id);
                        vehicles.RemoveAt(k);
                    }
                    break;
                case 5:
                    if (rng.Next(8) == 0) sim.Admin(new AdminCommand(AdminCommandType.ManualGreenRequest, dirs[rng.Next(4)]));
                    break;
                case 6:
                    if (rng.Next(8) == 0) sim.Admin(new AdminCommand(AdminCommandType.ReturnToAutomatic, null));
                    break;
                case 7:
                    if (rng.Next(40) == 0)
                        sim.Device(DeviceType.SignalController, null, rng.Next(2) == 0 ? DeviceStatus.Offline : DeviceStatus.Online);
                    break;
            }
            sim.Advance(Half);   // the engine itself also throws SafetyViolationException if desired state is ever unsafe
        }

        AssertCommandSequenceSafe(sim.Sent);
        Assert.NotEmpty(sim.Sent);
    }

    private static void AssertCommandSequenceSafe(IReadOnlyList<SendControllerCommand> sent)
    {
        SendControllerCommand? prev = null;
        foreach (var c in sent)
        {
            if (prev is not null && prev.CommandId == c.CommandId) continue;   // a retry, not a new command

            var greens = c.Signals.Where(kv => kv.Value == SignalState.Green).Select(kv => kv.Key).ToList();
            if (greens.Count > 0)
            {
                Assert.True(greens.All(g => IsNorthSouth(g) == IsNorthSouth(greens[0])), $"{c.CommandId}: conflicting greens");
                Assert.True(prev is null || prev.Signals.Values.All(s => s == SignalState.Red),
                    $"{c.CommandId}: GREEN must directly follow ALL_RED");
            }
            prev = c;
        }
    }

    // ------------------------------------------------------------------ vehicles & queues

    [Fact]
    public void Same_event_submitted_twice_changes_the_queue_once()
    {
        var sim = new Sim();
        var first = sim.Arrive(Direction.North, VehicleType.Truck, "VH-1", "evt-dup");
        var second = sim.Arrive(Direction.North, VehicleType.Truck, "VH-1", "evt-dup");

        Assert.Equal(Outcome.Accepted, first.Outcome);
        Assert.Equal(Outcome.Duplicate, second.Outcome);
        Assert.Equal(1, sim.QueueCount(Direction.North));
        Assert.Contains(sim.Audit, a => a.EventType == AuditEventType.DuplicateEventRejected);
    }

    [Fact]
    public void Same_vehicle_with_a_new_event_id_is_still_counted_once()
    {
        var sim = new Sim();
        sim.Arrive(Direction.North, VehicleType.Truck, "VH-1", "evt-a");
        sim.Arrive(Direction.North, VehicleType.Truck, "VH-1", "evt-b");
        Assert.Equal(1, sim.QueueCount(Direction.North));
    }

    [Fact]
    public void Clear_removes_the_vehicle_and_the_queue_never_goes_negative()
    {
        var sim = new Sim();
        sim.Arrive(Direction.North, VehicleType.Forklift, "VH-1");
        sim.Clear(Direction.North, "VH-1");
        Assert.Equal(0, sim.QueueCount(Direction.North));

        var orphan = sim.Clear(Direction.North, "VH-404");      // never arrived
        Assert.Equal(Outcome.Accepted, orphan.Outcome);
        Assert.Equal(0, sim.QueueCount(Direction.North));
    }

    [Fact]
    public void Out_of_order_clear_then_arrival_leaves_the_queue_empty()
    {
        var sim = new Sim();
        sim.Clear(Direction.East, "VH-9");
        sim.Arrive(Direction.East, VehicleType.Employee, "VH-9");
        Assert.Equal(0, sim.QueueCount(Direction.East));
    }

    [Fact]
    public void Invalid_events_are_rejected_without_touching_state()
    {
        var sim = new Sim();
        var now = sim.Now;

        var noVehicleType = sim.Engine.HandleSensorEvent(
            new SensorEvent("e1", "A", Direction.North, SensorEventType.VehicleArrived, "VH-1", null, 1, now), now);
        var stale = sim.Engine.HandleSensorEvent(
            new SensorEvent("e2", "A", Direction.North, SensorEventType.VehicleArrived, "VH-2", VehicleType.Truck, 2, now.AddHours(-1)), now);
        var future = sim.Engine.HandleSensorEvent(
            new SensorEvent("e3", "A", Direction.North, SensorEventType.VehicleArrived, "VH-3", VehicleType.Truck, 3, now.AddHours(1)), now);

        Assert.Equal(Outcome.Rejected, noVehicleType.Outcome);
        Assert.Equal(Outcome.Rejected, stale.Outcome);
        Assert.Equal(Outcome.Rejected, future.Outcome);
        Assert.Equal(0, sim.QueueCount(Direction.North));
    }

    // ------------------------------------------------------------------ emergency

    [Fact]
    public void Emergency_preemption_still_goes_through_yellow_and_all_red()
    {
        var sim = new Sim();
        sim.Advance(4);
        Assert.Equal(SignalState.Green, sim.Desired(Direction.North));

        var before = sim.Sent.Count;
        sim.Arrive(Direction.East, VehicleType.Emergency, "AMB-1");
        sim.Advance(12);

        var cmds = sim.NewCommands(before);
        Assert.Equal(3, cmds.Count);
        Assert.Equal(SignalState.Yellow, cmds[0].Signals[Direction.North]);
        Assert.Equal(SignalState.Red, cmds[0].Signals[Direction.East]);
        Assert.All(cmds[1].Signals.Values, s => Assert.Equal(SignalState.Red, s));
        Assert.Equal(SignalState.Green, cmds[2].Signals[Direction.East]);
        Assert.Equal(SignalState.Red, cmds[2].Signals[Direction.North]);
        Assert.Equal(OperatingMode.Emergency, sim.State.Mode);
    }

    [Fact]
    public void Competing_emergencies_are_served_first_come_first_served()
    {
        var sim = new Sim();
        sim.Advance(4);                                          // NS green
        sim.Arrive(Direction.East, VehicleType.Emergency, "AMB-1");
        sim.Advance(1);
        sim.Arrive(Direction.North, VehicleType.Emergency, "AMB-2");   // conflicts with the first
        sim.Advance(12);

        Assert.Equal(SignalState.Green, sim.Desired(Direction.East));
        Assert.Equal(SignalState.Red, sim.Desired(Direction.North));

        sim.Clear(Direction.East, "AMB-1");                      // first emergency done -> second one is served
        sim.Advance(12);
        Assert.Equal(SignalState.Green, sim.Desired(Direction.North));
        Assert.Equal(OperatingMode.Emergency, sim.State.Mode);

        sim.Clear(Direction.North, "AMB-2");
        Assert.Equal(OperatingMode.Automatic, sim.State.Mode);
    }

    [Fact]
    public void Stale_emergency_times_out_and_stops_blocking_the_junction()
    {
        var sim = new Sim();
        sim.Advance(4);
        sim.Arrive(Direction.East, VehicleType.Emergency, "AMB-1");
        sim.Advance(65);

        Assert.Equal(OperatingMode.Automatic, sim.State.Mode);
        Assert.Empty(sim.State.Emergencies);
        Assert.Equal(0, sim.QueueCount(Direction.East));
    }

    // ------------------------------------------------------------------ manual

    [Fact]
    public void Manual_request_uses_the_safe_sequence_and_can_be_returned_to_automatic()
    {
        var sim = new Sim();
        sim.Advance(4);
        var before = sim.Sent.Count;

        var result = sim.Admin(new AdminCommand(AdminCommandType.ManualGreenRequest, Direction.West, "admin-1"));
        Assert.Equal(Outcome.Accepted, result.Outcome);
        sim.Advance(12);

        var cmds = sim.NewCommands(before);
        Assert.Equal(SignalState.Yellow, cmds[0].Signals[Direction.North]);
        Assert.All(cmds[1].Signals.Values, s => Assert.Equal(SignalState.Red, s));
        Assert.Equal(SignalState.Green, cmds[2].Signals[Direction.West]);
        Assert.Equal(OperatingMode.Manual, sim.State.Mode);

        sim.Admin(new AdminCommand(AdminCommandType.ReturnToAutomatic, null));
        Assert.Equal(OperatingMode.Automatic, sim.State.Mode);
    }

    [Fact]
    public void Manual_override_expires_by_itself()
    {
        var sim = new Sim();
        sim.Advance(4);
        sim.Admin(new AdminCommand(AdminCommandType.ManualGreenRequest, Direction.West));
        sim.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(OperatingMode.Automatic, sim.State.Mode);
    }

    [Fact]
    public void Emergency_overrides_manual_and_manual_is_refused_during_emergency()
    {
        var sim = new Sim();
        sim.Advance(4);
        sim.Admin(new AdminCommand(AdminCommandType.ManualGreenRequest, Direction.West));
        sim.Arrive(Direction.North, VehicleType.Emergency, "AMB-1");
        Assert.Equal(OperatingMode.Emergency, sim.State.Mode);

        var refused = sim.Admin(new AdminCommand(AdminCommandType.ManualGreenRequest, Direction.West));
        Assert.Equal(Outcome.Conflict, refused.Outcome);
    }

    // ------------------------------------------------------------------ scheduling

    [Fact]
    public void Starvation_guard_serves_a_long_waiting_employee_even_when_the_other_side_is_much_heavier()
    {
        var cfg = new JunctionConfig { MaxWait = TimeSpan.FromSeconds(20) };
        var sim = new Sim(cfg: cfg);
        sim.Advance(4);                                          // NS green since ~t=2s
        sim.Arrive(Direction.East, VehicleType.Employee, "EMP-1");
        for (var i = 0; i < 8; i++) sim.Arrive(Direction.North, VehicleType.Truck, $"T-{i}");

        sim.Advance(24);                                         // employee has waited ~24s > MaxWait
        Assert.Contains(sim.Sent, c => c.Signals[Direction.North] == SignalState.Yellow);
    }

    [Fact]
    public void Without_starvation_pressure_a_much_heavier_side_keeps_its_green()
    {
        var sim = new Sim();                                     // MaxWait 60s, GreenDuration 30s
        sim.Advance(4);
        sim.Arrive(Direction.East, VehicleType.Employee, "EMP-1");
        for (var i = 0; i < 8; i++) sim.Arrive(Direction.North, VehicleType.Truck, $"T-{i}");

        sim.Advance(24);
        Assert.DoesNotContain(sim.Sent, c => c.Signals[Direction.North] == SignalState.Yellow);
    }

    [Fact]
    public void Green_is_not_switched_when_nobody_else_is_waiting()
    {
        var sim = new Sim();
        sim.Advance(4);
        var before = sim.Sent.Count;
        sim.Advance(90);
        Assert.Equal(before, sim.Sent.Count);                    // no needless switching
    }

    // ------------------------------------------------------------------ controller: ACK / failure / restart

    [Fact]
    public void Duplicate_ack_is_ignored()
    {
        var sim = new Sim();
        var first = sim.Sent[0];
        var again = sim.Engine.HandleControllerAck(new ControllerAck(first.CommandId, "A", AckStatus.Ack, null), sim.Now);
        Assert.Equal(Outcome.Duplicate, again.Outcome);
    }

    [Fact]
    public void Ack_that_contradicts_the_request_is_a_mismatch_and_is_retried_then_degraded()
    {
        var sim = new Sim(autoAck: false);
        var contradicting = Enum.GetValues<Direction>().ToDictionary(d => d, _ => SignalState.Green);   // controller claims green everywhere

        var result = sim.Engine.HandleControllerAck(
            new ControllerAck(sim.Sent[0].CommandId, "A", AckStatus.Ack, contradicting), sim.Now);

        Assert.Equal(OperatingMode.Degraded, sim.State.Mode);                   // conflicting physical state -> fail-safe
        Assert.Contains(sim.State.Alerts, a => a.Code == AlertCodes.UnsafeActual);
        Assert.Equal(Outcome.Accepted, result.Outcome);
    }

    [Fact]
    public void Controller_offline_then_online_recovers_through_a_confirmed_all_red()
    {
        var sim = new Sim();
        sim.Advance(4);
        sim.Device(DeviceType.SignalController, null, DeviceStatus.Offline);
        Assert.Equal(OperatingMode.Degraded, sim.State.Mode);
        Assert.All(sim.State.Desired.Values, s => Assert.Equal(SignalState.Red, s));

        sim.Device(DeviceType.SignalController, null, DeviceStatus.Online);     // auto-ACK confirms the all-red probe
        Assert.Equal(OperatingMode.Automatic, sim.State.Mode);

        sim.Advance(4);
        Assert.Contains(sim.State.Desired.Values, s => s == SignalState.Green);  // normal service resumes
    }

    [Fact]
    public void Restart_does_not_trust_previous_physical_state()
    {
        var sim = new Sim();
        sim.Advance(4);                                          // NS green, confirmed

        var json = JsonSerializer.Serialize(sim.State);          // what the DB snapshot would hold
        var restored = JsonSerializer.Deserialize<JunctionState>(json)!;
        var engine = new JunctionEngine(new JunctionConfig(), restored, () => "cmd-after-restart");

        var start = engine.Start(sim.Now);

        var send = Assert.Single(start.Effects.OfType<SendControllerCommand>());
        Assert.All(send.Signals.Values, s => Assert.Equal(SignalState.Red, s));
        Assert.All(restored.Actual.Values, s => Assert.Equal(SignalState.Unknown, s));
        Assert.Equal(SignalStage.AllRed, restored.Stage);

        // No ACK arrives: the engine must NOT hand out a GREEN in the meantime.
        var now = sim.Now;
        for (var i = 0; i < 8; i++)
        {
            now += Half;
            var tick = engine.Tick(now);
            Assert.DoesNotContain(tick.Effects.OfType<SendControllerCommand>(), c => c.Signals.Values.Contains(SignalState.Green));
        }
    }

    [Fact]
    public void Vehicle_queue_and_processed_event_ids_survive_a_snapshot_round_trip()
    {
        var sim = new Sim();
        sim.Arrive(Direction.North, VehicleType.Truck, "VH-1", "evt-keep");

        var restored = JsonSerializer.Deserialize<JunctionState>(JsonSerializer.Serialize(sim.State))!;
        var engine = new JunctionEngine(new JunctionConfig(), restored);
        engine.Start(sim.Now);

        var dup = engine.HandleSensorEvent(
            new SensorEvent("evt-keep", "A", Direction.North, SensorEventType.VehicleArrived, "VH-1", VehicleType.Truck, 99, sim.Now), sim.Now);

        Assert.Equal(Outcome.Duplicate, dup.Outcome);            // dedupe memory survived the restart
        Assert.Single(restored.Queues[Direction.North]);
    }
}
