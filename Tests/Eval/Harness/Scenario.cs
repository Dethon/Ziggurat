namespace Tests.Eval.Harness;

// One user request, everything that must hold afterwards, and how many times it must hold. Written
// as a typed record rather than as data in a file so that renaming a tool, a mount or a claim
// breaks the scenario at compile time instead of rotting it into a test of nothing.
public sealed record Scenario
{
    public required string Name { get; init; }

    // The shipped agent this runs as, by the id in Agent/appsettings.json. Naming it here rather
    // than building a spec is the point: a misconfigured model, provider routing, language or
    // prompt section has to fail the suite rather than be bypassed by it.
    public required string AgentId { get; init; }

    public required EvalTurn Turn { get; init; }

    // One instant, shared by the agent's decoration and by every server the scenario hosts, so an
    // expected fire time is an exact string rather than a range. It is the turn's timestamp too —
    // two values could disagree, and a scenario whose clock disagrees with itself proves nothing.
    public required DateTimeOffset Instant { get; init; }

    // Timers already running when the turn arrives, armed through the server's own create path
    // rather than written into its store: setup that bypassed the server would set up a state the
    // server cannot produce.
    public IReadOnlyList<ArmedTimerSeed> Armed { get; init; } = [];

    public IReadOnlyList<CallExpectation> Required { get; init; } = [];

    // Required plus permitted is the whole tolerated surface. A forbidden list was rejected in
    // ADR-0030's design because it cannot fail when a newly added tool starts being called for no
    // reason, which is precisely the regression this suite exists to catch.
    public IReadOnlyList<CallPermission> Permitted { get; init; } = [];

    // Declared only where order is part of the contract: tool invocation is concurrent by design,
    // so asserting the whole recorded sequence would report ordinary concurrency as a failure.
    public IReadOnlyList<OrderingConstraint> Ordering { get; init; } = [];

    // A model that flails through most of the allowed iterations and then answers correctly is not
    // passing, so the ceiling is part of the contract rather than a safety net.
    public required int CallCeiling { get; init; }

    // Everything in the home that this turn is allowed to move. The declaration is exhaustive:
    // an entity that changed and is not named here fails the scenario, which is the only way to
    // catch a permitted call that did more than it was asked to.
    public IReadOnlyList<StateChange> Changes { get; init; } = [];

    // What the answer itself must look like. Declared only where the reply is part of the
    // contract: most scenarios are about which tool ran, and a limit invented to fill this in
    // would fail on wording rather than on behaviour.
    public ReplyExpectation? Reply { get; init; }

    public IReadOnlyList<string> Claims { get; init; } = [];

    public RunPolicy Policy { get; init; } = RunPolicy.Once;

    // Which tier this scenario is a canary for, not which tier is running: the smoke tier takes
    // the ones marked Smoke, the full tier takes everything.
    public EvalTier Tier { get; init; } = EvalTier.Full;
}

// Everything a channel puts on a turn that the user did not type. The speaking-room rule is only
// decidable from these, so a scenario that omitted them would be testing a turn no channel sends.
public sealed record EvalTurn
{
    public required string Text { get; init; }

    public required string Sender { get; init; }

    public string? Room { get; init; }

    public string? SatelliteId { get; init; }

    public string? DismissedAlert { get; init; }
}

// How many of how many runs must pass. Never a retry-until-green: half the assertions in this
// suite are negative, and retrying past a failure hides exactly the regression being hunted.
public sealed record RunPolicy(int K, int N)
{
    public static RunPolicy Once { get; } = new(1, 1);
}

public enum EvalTier
{
    // One canary per family at a single run: what a prompt author runs while editing one section.
    Smoke,
    Full
}

// A call the scenario requires, matched by tool and by whatever its arguments have to say. The
// label is what an ordering constraint refers to, so the order of the list means nothing.
public sealed record CallExpectation
{
    public required string Label { get; init; }

    public required string Tool { get; init; }

    public IReadOnlyList<ArgumentMatcher> Arguments { get; init; } = [];
}

// A call the scenario tolerates, by tool and path only. Both accept `*`.
public sealed record CallPermission(string Tool, string Path = "*");

public sealed record OrderingConstraint(string Before, string After);

// One entity, and what the turn must leave it at. The key is an entity id, or an entity id and one
// of its attributes (`climate.salon#temperature`) — a thermostat set to another temperature has
// changed without its state moving, and a scenario about setting one has to be able to say so.
public sealed record StateChange(string Key, string To);

// A timer already running when the turn arrives, and — the part that matters — already partly
// spent. A timer armed at the pinned instant has its whole duration left, so reading its spec and
// reading its status answer the same number, and a scenario about reading the status cannot tell
// the two apart.
public sealed record ArmedTimerSeed(
    string Id, int DurationSeconds, string Room, TimeSpan RunningFor, string? Text = null);