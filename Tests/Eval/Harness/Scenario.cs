using Domain.DTOs;

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

    // Scripted exchanges that happened before the turn, seeded into the same run as prior
    // messages. Scripted rather than generated: a history the model wrote itself would differ
    // between runs, and what these scenarios are about is what the turn does with a conversation
    // that is already there — a worker, for one, starts without it.
    public IReadOnlyList<HistoryExchange> History { get; init; } = [];

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

    // Workers a turn may use without the scenario declaring what for. The delegation equivalent of
    // a permitted call, and needed for the same reason: a scenario whose subject is something else
    // should not fail because the model handed a lookup to a worker on the way there.
    public IReadOnlyList<string> MayDelegateTo { get; init; } = [];

    // What a canned worker answers. It has to read like a real result: a worker that comes back
    // with something obviously useless makes the parent redo the work itself, and the scenario
    // then measures the stub rather than the decision.
    public string WorkerAnswer { get; init; } =
        "Hecho. Te dejo el resultado resumido en dos líneas.";

    // Every task this turn is allowed to hand to a worker. Exhaustive like the state diff: a
    // delegation that is not declared here fails the scenario, which is how a scenario says that
    // a one-call lookup is done in place.
    public IReadOnlyList<DelegationExpectation> Delegates { get; init; } = [];

    // What must hold of a delegation if one happens. Nothing here requires the delegation — the
    // profile still needs its MayDelegateTo tolerance — but a run that does hand work to it is
    // held to the prompt it wrote. This is how a claim about a delegation's quality survives an
    // antecedent that is the model's own coin: the claims listed inside are tallied only over
    // the runs that delegated, so the scenario accepts both shapes without flaking on either.
    public IReadOnlyList<ConditionalDelegation> IfDelegated { get; init; } = [];

    // What the user is remembered as having said, injected the way recall injects it. Declaring
    // the facts is also declaring what must still be remembered afterwards: a forget-by-query is
    // one call that deletes everything its search reached, and the store after the turn is the
    // only place that shows.
    public IReadOnlyList<RememberedFact> Remembered { get; init; } = [];

    // What the files the turn touched must say afterwards.
    public IReadOnlyList<FileExpectation> Files { get; init; } = [];

    // What the answer itself must look like. Declared only where the reply is part of the
    // contract: most scenarios are about which tool ran, and a limit invented to fill this in
    // would fail on wording rather than on behaviour.
    public ReplyExpectation? Reply { get; init; }

    public IReadOnlyList<string> Claims { get; init; } = [];

    // Claims this scenario asserts without evidencing: the demonstrated-red bar was tried and
    // deleting the prose stayed green, so the behaviour is the model's own rather than the
    // prompt's. The note records how that demonstration went. Declared here rather than in a
    // table keyed by claim id so that a renamed or withdrawn claim breaks this file at compile
    // time; more than one scenario may guard the same claim.
    public IReadOnlyList<Guard> Guards { get; init; } = [];

    // Judgements the deterministic checks cannot make — whether an id is descriptive, whether a
    // reply narrates its steps — graded by a pinned judge model against each rubric. A verdict
    // failure fails the run like any other check; the claim is tallied in the scorecard as
    // judged. Graded only on runs the deterministic checks pass, so a judge is never paid to
    // grade a run that already failed.
    public IReadOnlyList<JudgedCheck> Judged { get; init; } = [];

    public RunPolicy Policy { get; init; } = RunPolicy.Once;

    // Which tier this scenario is a canary for, not which tier is running: the smoke tier takes
    // the ones marked Smoke, the full tier takes everything.
    public EvalTier Tier { get; init; } = EvalTier.Full;
}

// One earlier exchange: what the user said and what the assistant answered, verbatim. It rides
// the run as ordinary prior messages on the same channel as the turn, a few minutes apart.
public sealed record HistoryExchange(string User, string Assistant);

// Everything a channel puts on a turn that the user did not type. The speaking-room rule is only
// decidable from these, so a scenario that omitted them would be testing a turn no channel sends.
public sealed record EvalTurn
{
    public required string Text { get; init; }

    public required string Sender { get; init; }

    public string? Room { get; init; }

    public string? SatelliteId { get; init; }

    public string? DismissedAlert { get; init; }

    // The channel the turn came in on, for a scenario whose subject is where an answer is sent
    // back. Null means the chat, or voice when a satellite is named — the two every other scenario
    // already implies.
    public string? Channel { get; init; }
}

// How many of how many runs must pass. Never a retry-until-green: half the assertions in this
// suite are negative, and retrying past a failure hides exactly the regression being hunted.
public sealed record RunPolicy(int K, int N)
{
    public static RunPolicy Once { get; } = new(1, 1);

    // How many of the N runs may be in flight at once. All of them, because the runs are
    // independent — one full stack and one turn each — and taking them in turn made a family's
    // wall clock the sum of every run in it.
    //
    // What that costs is the early stop: a scenario whose threshold has already become
    // unreachable has its remaining runs already going, so a hard failure now spends the whole N
    // where a serial pass would have spent K-1 fewer. That is at most one extra run per scenario
    // that fails, on a pass that is red anyway, and it buys a uniform denominator — every
    // scorecard rate is over N — for the diff a model bump is read through. A scenario that
    // would rather have the saving asks for it by narrowing this.
    public int Width { get; init; } = N;
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

// A call the scenario tolerates, by tool, by path and — where the tool runs something — by the
// command it ran. All three accept `*`.
public sealed record CallPermission(string Tool, string Path = "*", string Command = "*")
{
    // Reading an action file's manual. The mount tells the agent to read an action's arguments
    // before writing one, and `--help` is an exec like any other — so a scenario whose subject is
    // which action ran has to tolerate the manual without tolerating the actions in that directory,
    // which a path-only permission cannot express.
    public static CallPermission Manual(string tool, string path) => new(tool, path, "*--help*");

    // Looking around, and reading the manuals of what is there. Every scenario whose subject is
    // which action ran needs exactly this pair, and writing it out per scenario is how one of them
    // ends up missing a line and reporting a `--help` as a behavioural failure.
    public static IReadOnlyList<CallPermission> LookingAndManuals(string path) =>
        [.. Looking(path), Manual(EvalTools.Exec, path)];

    // Looking before acting is tolerated almost everywhere in this suite, and spelling out the
    // same four lines per scenario is how one of them ends up missing and reporting a glob as a
    // behavioural failure.
    public static IReadOnlyList<CallPermission> Looking(string path) =>
    [
        new(EvalTools.Glob, path),
        new(EvalTools.Read, path),
        new(EvalTools.Info, path),
        new(EvalTools.Search, path)
    ];
}

public sealed record OrderingConstraint(string Before, string After);

// One guarded claim: asserted every run, never cited. The note is mandatory — it is the record
// of the demonstration that decided this claim cannot earn the citation, and the date stays a
// convention inside it rather than a field.
public sealed record Guard(string Claim, string Note);

// One judgement, aimed by the claim it grades. The rubric is the whole instruction: it has to
// say what passes and what fails in terms of the material the judge is shown, because the judge
// knows nothing about this suite beyond what one run's material carries.
public sealed record JudgedCheck(string Claim, string Rubric);

// One entity, and what the turn must leave it at. The key is an entity id, or an entity id and one
// of its attributes (`climate.salon#temperature`) — a thermostat set to another temperature has
// changed without its state moving, and a scenario about setting one has to be able to say so.
public sealed record StateChange(string Key, string To);

// One task handed to a worker: which profile ran it, and what the prompt had to carry. The worker
// starts with no conversation history, so every url, name and requirement the task needs has to be
// in the prompt the parent wrote — that is what `Carries` is asking about.
public sealed record DelegationExpectation
{
    public required string Profile { get; init; }

    public IReadOnlyList<string> Carries { get; init; } = [];
}

// The conditional half of a delegation contract: nothing requires the worker, but every
// delegation the profile does receive must carry these substrings — each worker starts with no
// history, so a prompt missing one is not self-contained whatever else it says — and the judged
// checks are graded only on the runs where a delegated prompt exists to be graded.
public sealed record ConditionalDelegation
{
    public required string Profile { get; init; }

    public IReadOnlyList<string> Carries { get; init; } = [];

    // Claims these conditions cite. Kept apart from the scenario's own list because their run
    // count differs: the scenario's claims are exercised every run, these only when the model
    // chose to delegate, and a scorecard that summed both under one denominator would report a
    // rate over runs that had no material.
    public IReadOnlyList<string> Claims { get; init; } = [];

    public IReadOnlyList<JudgedCheck> Judged { get; init; } = [];
}

// One file, and what it must say once the turn is over. Substrings rather than whole contents:
// what the vault contract protects is the syntax around the change, and a scenario that pinned the
// whole note would fail on the wording of the sentence the user asked for.
public sealed record FileExpectation
{
    public required string Path { get; init; }

    public IReadOnlyList<string> Contains { get; init; } = [];

    public IReadOnlyList<string> Absent { get; init; } = [];

    // Gone. A rename that copies and leaves the original behind updates every link correctly and
    // still leaves two notes where the user had one, and nothing else in the scenario notices.
    public bool Deleted { get; init; }

    // Byte-identical to before the turn. The strongest thing a scenario can say about a file it
    // did not ask to be edited, and the only one that catches an incidental rewrite whole.
    public bool Unchanged { get; init; }
}

// One remembered fact riding the turn, and whether this turn was supposed to end it. Category and
// importance are part of what the recall block says out loud, so a scenario about a correction can
// give the stale fact the weight a real one would have had.
public sealed record RememberedFact(string Content)
{
    public MemoryCategory Category { get; init; } = MemoryCategory.Fact;

    public double Importance { get; init; } = 0.8;

    // The turn must leave this one deleted. Everything else declared must survive it.
    public bool Forgotten { get; init; }
}

// A timer already running when the turn arrives, and — the part that matters — already partly
// spent. A timer armed at the pinned instant has its whole duration left, so reading its spec and
// reading its status answer the same number, and a scenario about reading the status cannot tell
// the two apart.
public sealed record ArmedTimerSeed(
    string Id, int DurationSeconds, string Room, TimeSpan RunningFor, string? Text = null);