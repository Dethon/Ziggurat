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
// They are two collections now, and the line between them is not free to move:
//
//   - A class holding a latency budget tighter than what a shared browser server adds under load
//     needs one nothing else is driving. Sharing it is not a slower version of the same
//     measurement; the run that tried it failed every such budget at once. Those take
//     QuietBrowserFixture and its own backend.
//   - Everything else shares the other backend. Several of them drive the fixture's one
//     PlaywrightWebBrowser and clear its cookies between tests, so they also have to stay
//     serialised with each other, which the one shared collection already does.
//
// Both halves are checked here rather than left to whoever adds the next class.
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

    // Two collections and no more: a third would be another set of pages on one of these two
    // backends, which is what the timing budgets cannot survive and what the shared browser's
    // cookie clearing cannot either.
    [Fact]
    public void EveryOtherClass_SharesTheOneCollectionThatSerialisesTheBrowserTheyPassAround()
    {
        var rest = BrowserTestClasses.Where(t => !HoldsATightBudget(t)).ToList();

        rest.ShouldNotBeEmpty();
        rest.ShouldAllBe(t => CollectionOf(t) == PlaywrightCollections.SharedBrowser);
    }
}