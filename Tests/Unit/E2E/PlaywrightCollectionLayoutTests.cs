using System.Reflection;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Integration.Fixtures;

namespace Tests.Unit.E2E;

// How the browser-integration classes are grouped into collections is what decides whether they
// wait for each other, and xUnit serialises a collection. All of them in one meant a chain as long
// as their spans added up to — about a minute, and on a run where the browser tests were the slow
// half, the whole suite finished behind it.
//
// They are three collections now, and the lines between them are not free to move:
//
//   - A class holding a latency budget tighter than what a shared browser server adds under load
//     needs one nothing else is driving. Sharing it is not a slower version of the same
//     measurement; the run that tried it failed every such budget at once. Those take
//     QuietBrowserFixture and its own backend.
//   - A class that clears the fixture's cookies is clearing the one context every session on that
//     backend shares, so it stays serialised with the others that do.
//   - Everything else opens its own GUID session and closes it, sharing no state any other class
//     can observe. Those get a backend of their own so they run beside the clearing chain rather
//     than behind it.
//
// All three are checked here rather than left to whoever adds the next class.
public class PlaywrightCollectionLayoutTests
{
    private static IReadOnlyList<Type> BrowserTestClasses =>
        [.. typeof(PlaywrightWebBrowserFixture).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters()
                    .Any(p => typeof(PlaywrightWebBrowserFixture).IsAssignableFrom(p.ParameterType))))];

    // [Collection] carries its name as a constructor argument and exposes no property for it, so
    // the attribute is read as data rather than as an instance.
    private static string CollectionOf(Type type) =>
        type.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(CollectionAttribute))
            .Select(a => a.ConstructorArguments[0].Value as string)
            .FirstOrDefault()
        ?? throw new InvalidOperationException($"{type.Name} takes the fixture but declares no collection");

    // Reading a stopwatch is not the question — one class gives a fail-fast path six seconds, which
    // no amount of shared-browser noise reaches, and it has no claim on a server of its own.
    // LatencyBudget is: a class reaches for it precisely when its budget is tight enough that the
    // host's own noise would otherwise decide the answer. So the marker is that reach, named rather
    // than inferred, and a refactor of how the milliseconds are asserted cannot silently move a
    // class out of this rule's sight.
    private static bool HoldsATightBudget(Type type) =>
        File.ReadAllText(SourceOf(type)).Contains(nameof(LatencyBudget), StringComparison.Ordinal);

    private static string SourceOf(Type type) =>
        Path.Combine(
            TestHelpers.FindSolutionRoot(), "Tests", "Integration", "Clients", $"{type.Name}.cs");

    [Fact]
    public void AClassHoldingALatencyBudget_TakesTheBrowserNothingElseIsDriving()
    {
        var timed = BrowserTestClasses.Where(HoldsATightBudget).ToList();

        timed.ShouldNotBeEmpty("no class holds a browser latency budget any more — this rule has nothing left to hold");
        timed.ShouldAllBe(t => CollectionOf(t) == PlaywrightCollections.Timing);
    }

    [Fact]
    public void TheTimingCollection_HoldsNothingThatIsNotTimingSomething()
    {
        var inTiming = BrowserTestClasses.Where(t => CollectionOf(t) == PlaywrightCollections.Timing);

        inTiming.ShouldAllBe(t => HoldsATightBudget(t));
    }

    // Clearing cookies reaches the one context every session on that backend shares, so a class
    // that does it cannot run beside a class it would clear underneath. That — not "browser test" —
    // is what SharedBrowser serialises, and it is why the marker is the call itself.
    private static bool ClearsTheSharedContext(Type type) =>
        File.ReadAllText(SourceOf(type))
            .Contains(nameof(PlaywrightWebBrowserFixture.ClearContextStateAsync), StringComparison.Ordinal);

    [Fact]
    public void AClassThatClearsTheSharedContext_StaysSerialisedWithTheOthersThatDo()
    {
        var clearing = BrowserTestClasses.Where(t => !HoldsATightBudget(t)).Where(ClearsTheSharedContext).ToList();

        clearing.ShouldNotBeEmpty("nothing clears the shared context any more — this rule has nothing left to hold");
        clearing.ShouldAllBe(t => CollectionOf(t) == PlaywrightCollections.SharedBrowser);
    }

    // A class that opens its own GUID session and closes it touches nothing another class can see,
    // so serialising it behind the cookie-clearing chain bought isolation nobody needed and cost
    // the run its tail: eleven classes in one collection ran end to end for the last twenty seconds
    // of the suite while the rest of the machine sat idle. These take a third backend and run
    // beside that chain instead.
    [Fact]
    public void AClassThatOnlyDrivesItsOwnSessions_RunsBesideTheCookieClearingChain()
    {
        var isolated = BrowserTestClasses
            .Where(t => !HoldsATightBudget(t))
            .Where(t => !ClearsTheSharedContext(t))
            .ToList();

        isolated.ShouldNotBeEmpty();
        isolated.ShouldAllBe(t => CollectionOf(t) == PlaywrightCollections.IsolatedSessions);
    }
}