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

    public IReadOnlyList<string> Claims { get; init; } = [];

    public RunPolicy Policy { get; init; } = RunPolicy.Once;

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