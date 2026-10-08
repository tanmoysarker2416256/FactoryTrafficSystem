namespace TrafficControl.Domain;

/// <summary>A traffic phase = a set of directions that may be GREEN together (they do not conflict).</summary>
public sealed record PhaseDefinition(string Id, IReadOnlyList<Direction> Directions);

/// <summary>
/// All junction-specific numbers and the phase plan live here, not inside the engine.
/// A new junction = a new JunctionConfig instance, the engine code does not change.
/// </summary>
public sealed class JunctionConfig
{
    public const string NorthSouth = "NORTH_SOUTH";
    public const string EastWest = "EAST_WEST";

    public string JunctionId { get; init; } = "A";
    public string Name { get; init; } = "Junction A";

    /// <summary>Two phases that conflict with each other. Any two different phases are treated as conflicting.</summary>
    public IReadOnlyList<PhaseDefinition> Phases { get; init; } = new[]
    {
        new PhaseDefinition(NorthSouth, new[] { Direction.North, Direction.South }),
        new PhaseDefinition(EastWest,   new[] { Direction.East,  Direction.West  })
    };

    // ---- signal timing ----
    public TimeSpan Yellow { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan AllRed { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MinGreen { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan GreenDuration { get; init; } = TimeSpan.FromSeconds(30);   // normal green
    public TimeSpan MaxGreen { get; init; } = TimeSpan.FromSeconds(60);        // green may be extended up to this

    // ---- scheduling ----
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(60);         // starvation guard
    public double SwitchThreshold { get; init; } = 1.5;                         // hysteresis against flip-flopping
    public double WaitBonusPerSecond { get; init; } = 0.1;
    public double AssumedDemandWhenSensorOffline { get; init; } = 2.0;
    public Dictionary<VehicleType, double> VehicleWeights { get; init; } = new()
    {
        [VehicleType.Emergency] = 100,
        [VehicleType.Truck] = 5,
        [VehicleType.Forklift] = 3,
        [VehicleType.Employee] = 1
    };

    // ---- modes ----
    public TimeSpan ManualTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan EmergencyTimeout { get; init; } = TimeSpan.FromSeconds(60);

    // ---- controller communication ----
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxAckRetries { get; init; } = 2;                                // total sends = 1 + retries
    public TimeSpan DegradedProbeInterval { get; init; } = TimeSpan.FromSeconds(15);

    // ---- sensor event validation ----
    public TimeSpan MaxEventAge { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaxFutureSkew { get; init; } = TimeSpan.FromMinutes(1);

    public IEnumerable<Direction> AllDirections => Phases.SelectMany(p => p.Directions);

    public PhaseDefinition GetPhase(string id) =>
        Phases.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"Unknown phase '{id}'.");

    public PhaseDefinition PhaseOf(Direction d) =>
        Phases.FirstOrDefault(p => p.Directions.Contains(d)) ?? throw new ArgumentException($"Direction {d} is not part of this junction.");

    public double Weight(VehicleType t) => VehicleWeights.TryGetValue(t, out var w) ? w : 1;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(JunctionId)) throw new ArgumentException("JunctionId is required.");
        if (Phases.Count < 2) throw new ArgumentException("A junction needs at least two phases.");
        var dirs = AllDirections.ToList();
        if (dirs.Count != dirs.Distinct().Count()) throw new ArgumentException("A direction can belong to only one phase.");
        if (Yellow <= TimeSpan.Zero || AllRed <= TimeSpan.Zero || AckTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Yellow, AllRed and AckTimeout must be positive.");
        if (MinGreen > GreenDuration || GreenDuration > MaxGreen)
            throw new ArgumentException("Require MinGreen <= GreenDuration <= MaxGreen.");
    }
}
