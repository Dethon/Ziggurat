using System.Net;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.Security;
using McpServerSugary.Store;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// A built app's reads, as the apps server forwards them to the agent: its own credential, the viewer's
// `apps` assertion, and the app (id, building agent, reads). The agent reads through the building agent's
// mounts as a turn of it would, signed as the viewer, against servers in process over the MCP wire.
public sealed class AppReadsApiTests
{
    private static readonly AppReadScope _glucoseApp = new() { Id = "abc123", Agent = "jonas", Reads = ["/sugary"] };

    // Her and Him share a household and keep their own logs.
    private static readonly HouseholdSugaryPeople _people = new(
    [
        new SugaryPerson(SugaryServer.Her.UserId, "Her", ReadsEveryLog: false),
        new SugaryPerson(SugaryServer.Him.UserId, "Him", ReadsEveryLog: false)
    ]);

    [Fact]
    public async Task TwoViewersOfOneApp_EachReadTheirOwnSugaryRows()
    {
        await using var sugary = await SugaryServer.StartAsync(sugaryPeople: _people);
        await SaveMealAsync(sugary, SugaryServer.Her, "her lentils");
        await SaveMealAsync(sugary, SugaryServer.Him, "his paella");
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, Jonas(sugary.Endpoint));
        await using var agent = await AppReadsAgent.StartAsync(households, new SharedServers { Sugary = sugary.Endpoint });

        (await NotesAsync(agent, SugaryServer.Her)).ShouldBe(["her lentils"]);
        (await NotesAsync(agent, SugaryServer.Him)).ShouldBe(["his paella"]);
    }

    private static async Task<IReadOnlyList<string?>> NotesAsync(AppReadsAgent agent, Account viewer)
    {
        using var response = await agent.ReadAsync(
            AppReads.Operations.Query, _glucoseApp,
            new JsonObject { ["path"] = "/sugary", ["sql"] = "select notes from meals" },
            TestAssertions.Of(viewer, AppChannels.Apps));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return [.. body["rows"]!.AsArray().Select(row => row![0]?.GetValue<string>())];
    }

    private static AgentDefinition Jonas(params string[] endpoints) => new()
    {
        Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = endpoints, EnabledFeatures = ["filesystem"],
        WhitelistPatterns = ["domain__filesystem__*"]
    };

    private static async Task SaveMealAsync(SugaryServer server, Account person, string notes)
    {
        await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
        db.Meals.Add(new Meal
        {
            Id = Guid.NewGuid(),
            EatenAt = DateTimeOffset.Parse("2026-10-11T12:00:00Z"),
            Notes = notes,
            Outcome = MealOutcome.High,
            FirstEstimate = new Interval(30, 40, 55),
            Items = [],
            CarbsLow = 30,
            Carbs = 40,
            CarbsHigh = 55
        });
        await db.SaveChangesAsync();
    }
}