using System.Text.Json.Nodes;
using McpServerSugary.Glucose;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.McpServerSugary;

// The server reminds her of her period by the forecast the app draws (cycle.ts, forecast): both read
// Sugary.Client/src/cases/forecast.json, so a reminder never names another day than the app shows.
public class CycleForecastTests
{
    public static TheoryData<string> Cases() =>
        [.. Read()["cases"]!.AsArray().Select(c => c!["name"]!.GetValue<string>())];

    private static JsonNode Read() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(McpServerRegistrations.RepoRoot, "Sugary.Client", "src", "cases", "forecast.json")))!;

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheServersForecast_IsTheApps(string name)
    {
        var c = Read()["cases"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var periods = c["starts"]!.AsArray()
            .Select(s => DateOnly.Parse(s!.GetValue<string>()))
            .Select(start => new CyclePeriod(start, start.AddDays(4)))
            .ToList();

        var read = CycleForecast.Of(periods, DateOnly.Parse(c["today"]!.GetValue<string>()));

        if (c["expected"] is not { } expected)
        {
            read.ShouldBeNull();
            return;
        }

        read.ShouldNotBeNull();
        read.Earliest.ShouldBe(DateOnly.Parse(expected["earliest"]!.GetValue<string>()));
        read.Latest.ShouldBe(DateOnly.Parse(expected["latest"]!.GetValue<string>()));
        read.Likeliest.ShouldBe(DateOnly.Parse(expected["likeliest"]!.GetValue<string>()));
    }
}