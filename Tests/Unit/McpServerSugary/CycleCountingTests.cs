using System.Text.Json.Nodes;
using McpServerSugary.Glucose;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.McpServerSugary;

// The server counts her cycles as the app does (cycle.ts, countCycles): both read
// Sugary.Client/src/cases/cycles.json, so her reminder, her glucose by phase and the app's forecast and
// trends never read her cycles differently.
public class CycleCountingTests
{
    public static TheoryData<string> Cases() =>
        [.. Read()["cases"]!.AsArray().Select(c => c!["name"]!.GetValue<string>())];

    private static JsonNode Read() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(McpServerRegistrations.RepoRoot, "Sugary.Client", "src", "cases", "cycles.json")))!;

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheServersCount_IsTheApps(string name)
    {
        var c = Read()["cases"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var starts = c["starts"]!.AsArray().Select(s => DateOnly.Parse(s!.GetValue<string>())).ToList();
        var expected = c["expected"]!;

        var read = CycleCounting.Of(starts);

        read.Anchor.ShouldBe(expected["anchor"] is { } anchor ? DateOnly.Parse(anchor.GetValue<string>()) : null);
        read.Cycles.ShouldBe(
        [
            .. expected["cycles"]!.AsArray().Select(x => new CountedCycle(
                DateOnly.Parse(x!["start"]!.GetValue<string>()),
                x["days"]!.GetValue<int>(),
                x["skipped"]!.GetValue<bool>(),
                x["joined"]!.GetValue<bool>()))
        ]);
    }
}