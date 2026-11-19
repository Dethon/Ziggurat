using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Domain.Tools.Sugary.Watches;
using Domain.Tools.Watches;
using Infrastructure.Agents.Mcp;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit.Domain.Tools.FileSystem;

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

    // As the agent reaches /watchers: a union of the home's watches and Sugary's over the wire. Him writing
    // a home watch under an id her glucose watch holds, which he cannot see, is refused saying nothing of
    // it — the stat that tells the union so crosses the wire with the rest of the answer.
    [Fact]
    public async Task AHomeWatchUnderAnIdHerGlucoseWatchHolds_IsRefusedOverTheWire()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: _people);
        Ok(await CreateAsync(server, "low", Low, SugaryServer.Her));
        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(server.Endpoint));
        var home = new EntryMount("ha-watches", []);
        var union = (ICallerBoundBackend)new UnionFileSystem(
        [
            new UnionFileSystem.Member("ha-watches", new FileSystemUnion(Watchers.RouteBy, [], Fallback: true), home),
            new UnionFileSystem.Member(SugaryWatchFileSystem.Name, new FileSystemUnion(Watchers.RouteBy, [.. GlucoseTrigger.Kinds]),
                new McpFileSystemBackend(client, SugaryWatchFileSystem.Name, null))
        ]);

        var refused = await union.As(SugaryServer.TurnOf(SugaryServer.Him)).CreateAsync(
            "/low/watch.json", """{"triggers":[{"trigger":"state"}]}""", false, true, CancellationToken.None);
        var free = await union.As(SugaryServer.TurnOf(SugaryServer.Him)).CreateAsync(
            "/door/watch.json", """{"triggers":[{"trigger":"state"}]}""", false, true, CancellationToken.None);

        var error = refused.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.AlreadyExists);
        error.Message.ShouldNotContain(SugaryServer.Her.UserId);
        error.Message.ShouldNotContain("glucose");
        home.Files.Keys.ShouldNotContain("/low/watch.json");
        free.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        home.Files.Keys.ShouldContain("/door/watch.json");
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