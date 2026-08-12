using Xunit.Abstractions;

[assembly: TestCollectionOrderer("Tests.HeavyFirstCollectionOrderer", "Tests")]

namespace Tests;

// The run is as long as its longest collection, and the longest ones here are the two that have to
// bring something up before they can begin: the WebChat compose stack and the lemonade container.
// xUnit hands collections out in discovery order and fills every thread it has, so those two were
// starting behind three and a half thousand unit tests — the stack's containers were still booting
// at half a minute in, and everything waiting on them finished that much later.
//
// Ordering is not scheduling: this only decides who is offered a thread first, and the unit tests
// still run in parallel beside them. What it buys is that a boot which nothing can shorten is
// already underway while the cheap work goes past it, instead of after.
public class HeavyFirstCollectionOrderer : ITestCollectionOrderer
{
    // Longest first, and only the ones whose cost is a container. Everything else keeps discovery
    // order, which is what xUnit would have done with all of them.
    private static readonly string[] _bootsSomething =
    [
        "WebChatE2E.",
        "Lemonade",
        "DashboardE2E",
        "PlaywrightWebBrowserIntegration",
    ];

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections.OrderBy(c => Rank(c.DisplayName));

    private static int Rank(string displayName)
    {
        var index = Array.FindIndex(
            _bootsSomething, prefix => displayName.Contains(prefix, StringComparison.Ordinal));
        return index < 0 ? _bootsSomething.Length : index;
    }
}