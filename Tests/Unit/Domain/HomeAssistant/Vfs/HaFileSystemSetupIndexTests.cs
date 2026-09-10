using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Prompts;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// The setup index is a file at the mount's root, built on every read from the same sources that
// built it when it was appended to the prompt. What these pin is that it is the same text, and
// that it is as old as the read rather than as old as the conversation.
public class HaFileSystemSetupIndexTests
{
    private static (HaFileSystem Fs, FakeHaClient Client, HaCatalogProvider Provider, FakeTimeProvider Clock) Build()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[]}"""
        };
        var clock = new FakeTimeProvider();
        var provider = new HaCatalogProvider(() => client, clock);
        return (new HaFileSystem(provider, () => client, timeProvider: clock, satellites: new FakeSatellites()), client, provider, clock);
    }

    [Fact]
    public async Task GlobAsync_TheRoot_ListsTheSetupIndex()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.GlobAsync("", "*", CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries.ShouldContain(HaVfsPath.SetupIndexFileName);
    }

    [Fact]
    public async Task InfoAsync_TheSetupIndex_IsAFileThatExists()
    {
        var (fs, _, _, _) = Build();

        var info = (await fs.InfoAsync(HaVfsPath.SetupIndexFileName, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;

        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(false);
    }

    [Fact]
    public async Task ReadAsync_TheSetupIndex_IsWhatTheSummaryBuilderYields()
    {
        var (fs, client, provider, clock) = Build();

        var read = (await fs.ReadAsync(HaVfsPath.SetupIndexFileName, null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        // The same builder over the same sources — catalog and watches — so the file is the text
        // the served prompt used to carry, watches line included.
        // A read numbers its lines, as every text read on this mount does; the words are the same.
        Unnumbered(read.Content).TrimEnd().ShouldBe(
            (await new HomeAssistantSetupSummary(provider, new HaWatches(() => client, clock), new FakeSatellites())
                .GetAsync(CancellationToken.None)).TrimEnd());
        read.Content.ShouldContain("watches:");
        read.Content.ShouldContain("voice satellites: kitchen-01 (room \"Kitchen\")");
        read.Content.ShouldContain("## Current Home Assistant setup");
        read.Content.ShouldContain("light.kitchen_(kitchen)");
    }

    // A device added this morning is known this afternoon, in a conversation that started last
    // week: the file is built from the catalog as it is at the read, not as it was at warmup.
    [Fact]
    public async Task ReadAsync_AfterAnEntityAppears_ShowsItOnTheNextRead()
    {
        var (fs, client, _, clock) = Build();
        (await Read(fs)).ShouldNotContain("switch.new_plug");

        client.States.Add(Entity("switch.new_plug", "off", ("friendly_name", JsonValue.Create("New Plug"))));
        clock.Advance(TimeSpan.FromMinutes(6));

        (await Read(fs)).ShouldContain("switch.new_plug_(new-plug)");
    }

    private sealed class FakeSatellites : ISatelliteCatalog
    {
        public Task<IReadOnlyList<SatelliteDescriptor>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SatelliteDescriptor>>([new("kitchen-01", "Kitchen")]);

        public Task<IReadOnlyList<string>> ResolveAsync(AnnounceTarget target, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static string Unnumbered(string content) =>
        string.Join("\n", content.Split('\n').Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^\d+: ?", "")));

    private static async Task<string> Read(HaFileSystem fs) =>
        (await fs.ReadAsync(HaVfsPath.SetupIndexFileName, null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;
}