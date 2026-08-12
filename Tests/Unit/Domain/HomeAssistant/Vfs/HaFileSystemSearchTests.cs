using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemSearchTests
{
    private const string KitchenStateFile = "entities/light/kitchen_(kitchen)/state.json";

    private static HaFileSystem Build()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))),
                Entity("light.salon", "on", ("friendly_name", JsonValue.Create("Salon"))),
                Entity("sensor.salon_temp", "21", ("friendly_name", JsonValue.Create("Salon Temp")))
            },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["light.salon","sensor.salon_temp"]}]}"""
        };
        return new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);
    }

    [Fact]
    public async Task SearchAsync_DirectoryPath_ScopesToClass()
    {
        var result = await Build().SearchAsync(
            "state", false, null, "entities/sensor", null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        var search = result.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.FilesSearched.ShouldBe(1);
        search.Results.Select(r => r.File).ShouldAllBe(f => f.Contains("/sensor/"));
    }

    [Fact]
    public async Task SearchAsync_DirectoryPath_ScopesToArea()
    {
        var result = await Build().SearchAsync(
            "state", false, null, "areas/salon", null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        var search = result.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.FilesSearched.ShouldBe(2);
        search.Results.Select(r => r.File).ShouldNotContain(f => f.Contains("kitchen"));
    }

    [Fact]
    public async Task SearchAsync_SingleFilePath_ScopesToOneEntity()
    {
        var result = await Build().SearchAsync(
            "state", false, KitchenStateFile, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        var search = result.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.FilesSearched.ShouldBe(1);
        search.Results[0].File.ShouldContain("kitchen");
    }

    [Fact]
    public async Task SearchAsync_FilesOnlyOutputMode_ReturnsMatchCountWithoutMatches()
    {
        var result = await Build().SearchAsync(
            "state", false, null, null, null, 50, 1, VfsTextSearchOutputMode.FilesOnly, CancellationToken.None);
        var first = result.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value.Results[0];
        first.MatchCount.ShouldNotBeNull();
        first.Matches.ShouldBeNull();
    }

    [Fact]
    public async Task SearchAsync_ContextLines_IncludesSurroundingLines()
    {
        var result = await Build().SearchAsync(
            "attributes", false, KitchenStateFile, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        var match = result.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value.Results[0].Matches![0];
        match.Context.ShouldNotBeNull();
        match.Context!.After.ShouldContain(l => l.Contains("friendly_name"));
    }

    [Fact]
    public async Task SearchAsync_FilePattern_NonMatching_ReturnsNoResults()
    {
        var result = await Build().SearchAsync(
            "state", false, null, null, "*.sh", 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        var search = result.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.FilesSearched.ShouldBe(0);
        search.TotalMatches.ShouldBe(0);
        search.Results.Count.ShouldBe(0);
    }

    [Fact]
    public async Task SearchAsync_InvalidRegex_ReturnsInvalidArgumentWithHint()
    {
        var result = await Build().SearchAsync(
            "(unclosed", true, null, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        var error = result.ShouldBeOfType<FsResult<FsSearchResult>.Err>().Error;
        error.ErrorCode.ShouldBe("invalid_argument");
        error.Hint.ShouldNotBeNull().ShouldContain("regex=false");
    }

    [Fact]
    public async Task SearchAsync_MatchesExactlyMaxResults_NotTruncated()
    {
        // "entity_id" appears once per state file → exactly 3 matches across 3 entities. Hitting the
        // cap exactly, with nothing left unsearched, is NOT truncation.
        var result = await Build().SearchAsync(
            "entity_id", false, null, null, null, 3, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);

        var search = result.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.TotalMatches.ShouldBe(3);
        search.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task SearchAsync_StopsAtMaxResults_ReportsOnlyFilesActuallySearched()
    {
        // maxResults=1 fills on the first entity; the remaining two are never scanned, so filesSearched
        // must reflect what was actually read (1), not the full scope (3), and truncated must be true.
        var result = await Build().SearchAsync(
            "entity_id", false, null, null, null, 1, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);

        var search = result.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.TotalMatches.ShouldBe(1);
        search.Truncated.ShouldBeTrue();
        search.FilesSearched.ShouldBe(1);
    }

    [Fact]
    public async Task SearchAsync_UsesLiveState_NotCachedCatalog()
    {
        // Swapping the whole client (rather than mutating States in place) models the real client,
        // which returns a fresh snapshot per call — so the cached catalog keeps the stale snapshot.
        var client = new FakeHaClient { States = { Entity("light.kitchen", "stalestate") } };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        // Warm the structure cache with the original state.
        var before = await fs.SearchAsync(
            "stalestate", false, null, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        before.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value.TotalMatches.ShouldBeGreaterThan(0);

        // State changes within the same agent loop (the cache TTL has NOT elapsed) — search must see it.
        client = new FakeHaClient { States = { Entity("light.kitchen", "freshstate") } };

        var after = await fs.SearchAsync(
            "freshstate", false, null, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        after.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value.TotalMatches.ShouldBeGreaterThan(0);
    }
}