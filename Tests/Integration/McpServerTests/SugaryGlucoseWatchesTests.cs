using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Tools;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Glucose watches as the agent writes them: over MCP, on the sugary-watches filesystem, kept in
// Sugary's database for the household, shown to whom they concern.
public class SugaryGlucoseWatchesTests
{
    private const string Low = """
        {"name":"Low","triggers":[{"trigger":"glucose","below":70,"for":{"minutes":10}}],
         "effects":[{"kind":"prompt","prompt":"Tell her she is low"}]}
        """;

    private static readonly HouseholdSugaryPeople _people = new([
        new SugaryPerson(SugaryServer.Her.UserId, "Her", false),
        new SugaryPerson(SugaryServer.Him.UserId, "Him", false)
    ]);

    [Fact]
    public async Task AWatch_IsKeptForTheHousehold_AndShownToWhomItConcerns()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: _people);

        Ok(await CreateAsync(server, "low", Low, SugaryServer.Her))["status"]!.GetValue<string>().ShouldBe("created");

        (await GlobAsync(server, SugaryServer.Her)).ShouldBe(["low/"]);
        (await GlobAsync(server, SugaryServer.Him)).ShouldBeEmpty();
        var read = await server.CallAsync("fs_read", new Dictionary<string, object?>
        {
            ["filesystem"] = "sugary-watches", ["path"] = "low/watch.json"
        });
        read["content"]!.GetValue<string>().ShouldContain("\"below\": 70");

        var taken = await CreateAsync(server, "low", Low, SugaryServer.Him);
        taken["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.AlreadyExists);

        Ok(await server.CallAsync("fs_delete", new Dictionary<string, object?>
        {
            ["filesystem"] = "sugary-watches", ["path"] = "low"
        }));
        (await GlobAsync(server, SugaryServer.Her)).ShouldBeEmpty();
    }

    // The engine's judgments are keyed by a trigger's place in the list, so a watch written anew forgets
    // them, and a watch removed takes them with it.
    [Fact]
    public async Task WhatWasJudgedOfAWatch_GoesWhenItIsWrittenAnewOrRemoved()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: _people);
        Ok(await CreateAsync(server, "low", Low, SugaryServer.Her));
        Ok(await CreateAsync(server, "high", Low.Replace("\"below\":70", "\"above\":250"), SugaryServer.Her));
        await JudgeAsync(server, "low");
        await JudgeAsync(server, "high");

        var status = JsonNode.Parse((await server.CallAsync("fs_read", new Dictionary<string, object?>
        {
            ["filesystem"] = "sugary-watches", ["path"] = "low/status.json"
        }))["content"]!.GetValue<string>().Split('\n').Select(line => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..])
            .Aggregate((a, b) => a + "\n" + b))!;
        status["triggers"]![0]!["holds"]!.GetValue<bool>().ShouldBeTrue();

        Ok(await server.CallAsync("fs_edit", new Dictionary<string, object?>
        {
            ["filesystem"] = "sugary-watches",
            ["path"] = "low/watch.json",
            ["edits"] = new[] { new Dictionary<string, object?> { ["oldString"] = "\"below\": 70", ["newString"] = "\"below\": 65" } }
        }));
        Ok(await server.CallAsync("fs_delete", new Dictionary<string, object?>
        {
            ["filesystem"] = "sugary-watches", ["path"] = "high"
        }));

        await using var db = await Factory(server).CreateDbContextAsync();
        (await db.GlucoseWatchStates.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await db.GlucoseWatches.IgnoreQueryFilters().Select(w => w.Id).ToListAsync()).ShouldBe(["low"]);
    }

    private static async Task JudgeAsync(SugaryServer server, string id)
    {
        await using var db = await Factory(server).CreateDbContextAsync();
        db.GlucoseWatchStates.Add(new GlucoseWatchState
        {
            TenantId = SugaryServer.Her.TenantId,
            WatchId = id,
            TriggerIndex = 0,
            Holding = true,
            HoldingSince = DateTimeOffset.UtcNow,
            FiredThisEpisode = true
        });
        await db.SaveChangesAsync();
    }

    private static IDbContextFactory<SugaryDbContext> Factory(SugaryServer server) =>
        server.Services.GetRequiredService<IDbContextFactory<SugaryDbContext>>();

    private static Task<JsonNode> CreateAsync(SugaryServer server, string id, string content, Account person) =>
        server.CallAsync("fs_create", new Dictionary<string, object?>
        {
            ["filesystem"] = "sugary-watches",
            ["path"] = $"{id}/watch.json",
            ["content"] = content
        }, SugaryServer.TurnOf(person));

    private static async Task<IReadOnlyList<string>> GlobAsync(SugaryServer server, Account person)
    {
        var glob = await server.CallAsync("fs_glob", new Dictionary<string, object?>
        {
            ["filesystem"] = "sugary-watches", ["pattern"] = "*/", ["basePath"] = ""
        }, SugaryServer.TurnOf(person));
        return glob["entries"]?.AsArray().Select(e => e!.GetValue<string>()).ToList()
               ?? throw new InvalidOperationException(glob.ToJsonString());
    }

    private static JsonNode Ok(JsonNode answer)
    {
        answer["ok"]?.GetValue<bool>().ShouldNotBe(false, answer.ToJsonString());
        return answer;
    }
}