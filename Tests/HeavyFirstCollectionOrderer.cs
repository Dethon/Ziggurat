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
        // The classes that wait on a real model come first, by name: their time is the provider's
        // and costs the machine almost nothing, but each is a serial chain of turns, and one offered
        // its thread late ends the run — McpAgentFileSystemTests started at 77 s and finished at 124.
        // Prefixes would not do: "McpAgent" matched fourteen classes, most of them quick unit tests,
        // and pushed these past the threads that start at once.
        "McpAgentFileSystemTests",
        "McpAgentTests",
        "McpAgentMultiFileSystemTests",
        "McpAgentReasoningTests",
        "MemoryExtractionResponseFormatTests",
        "MemoryProfileSynthesisResponseFormatTests",
        "MemoryConsolidationResponseFormatTests",
        "SubAgentTests",
        "ThreadSessionTests",
        "ToolApprovalChatClientTests",
        "OpenRouterToolCallingWithReasoningTests",
        "WebChatE2E.",
        // Every Camoufox collection — the shared chain, the timing pair and the isolated lanes —
        // waits on a browser container before its first test, and the class pinning a fingerprint
        // launches two of its own.
        "Playwright",
        "CamoufoxFingerprintPinTests",
        // Longer chains before shorter ones: there are more of these than threads, and a chain of
        // half a minute offered its thread late is what the run then ends on (EvalWebTests started
        // at 54 s behind the short model classes below and finished at 100).
        "EvalWebTests",
        "SugaryClip",
        "DashboardE2E",
        "DashboardAdminE2E",
        // The Lemonade container's chain by name: "Lemonade" matches nineteen classes.
        "LemonadeEntrypointConfigTests",
        // The longest classes that start nothing but their own in-process servers: a class is
        // serialised, so its sum is its length.
        "McpWebBrowseAsMeTests",
        // Home Assistant cold-starts on a freshly seeded /config and does not serve its API until
        // the integration loader has run — seconds on an idle machine, but it was being offered a
        // thread behind every unit test and then racing a three-minute readiness cap against a
        // daemon busy starting everything else. It boots something, so it belongs here.
        "HomeAssistantClientTests",
        // The integration classes that otherwise start last and end the run.
        "HouseholdMigrationCommandTests",
        "SugaryPeopleApiTests",
        "HouseholdChannelServersTests",
        "Lemonade",
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