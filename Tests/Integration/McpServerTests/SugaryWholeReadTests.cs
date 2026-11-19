using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using McpServerSugary.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A table's file is read in chunks, one call each — the agent's proxy asks 256 KiB at a time, and a
// command in the sandbox reads through it — while the poller writes a reading a minute. Every chunk
// of one read is the same version of the file, the size info gave is the size the read returns, and
// the next read after it sees what landed meanwhile. Chunks here are small so every file is several.
public class SugaryWholeReadTests
{
    private const string October = "/glucose/2026-10.parquet";
    private const int Chunk = 200;
    private static readonly DateTimeOffset _first = DateTimeOffset.Parse("2026-10-01T08:00:00Z");

    private sealed record Harness(SugaryServer Server, FakeTimeProvider Clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    private static async Task<Harness> StartAsync()
    {
        var clock = new FakeTimeProvider(_first);
        var server = await SugaryServer.StartAsync(services: c =>
            c.Replace(ServiceDescriptor.Singleton<IReadSnapshots>(new ReadSnapshots(clock))));
        await server.SaveAsync([.. Enumerable.Range(0, 30).Select(minute => Reading(minute, 100 + minute))]);
        return new Harness(server, clock);
    }

    private static GlucoseReading Reading(int minutes, double mgDl) => new()
    {
        At = _first + TimeSpan.FromMinutes(minutes),
        MgPerDl = mgDl,
        Trend = GlucoseTrend.Steady,
        Source = GlucoseSource.Current
    };

    private static async Task<JsonNode> BlobAsync(SugaryServer server, long offset) =>
        await server.CallAsync("fs_blob_read", new Dictionary<string, object?>
        {
            ["path"] = October,
            ["offset"] = offset,
            ["length"] = Chunk
        });

    // The rest of a read from where it got to; between is run after each chunk.
    private static async Task<byte[]> ReadOnAsync(SugaryServer server, List<byte> bytes, Func<Task>? between = null)
    {
        while (true)
        {
            var chunk = await BlobAsync(server, bytes.Count);
            bytes.AddRange(Convert.FromBase64String(chunk["contentBase64"]?.GetValue<string>()
                                                    ?? throw new InvalidOperationException(chunk.ToJsonString())));
            if (chunk["eof"]!.GetValue<bool>())
            {
                return [.. bytes];
            }

            if (between is not null)
            {
                await between();
            }
        }
    }

    private static async Task<long> SizeAsync(SugaryServer server) =>
        (await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = October }))["size"]!.GetValue<long>();

    [Fact]
    public async Task AReadingLandingBetweenTwoChunks_IsNotInTheReadAlreadyGoing()
    {
        await using var harness = await StartAsync();
        var server = harness.Server;
        var size = await SizeAsync(server);
        // A stat, then the read: one lands between them too.
        await server.SaveAsync(Reading(29 + 60, 300));
        var minute = 30;

        var bytes = await ReadOnAsync(server, [], async () =>
        {
            await server.SaveAsync(Reading(minute++, 300));
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
        });

        minute.ShouldBeGreaterThan(31, "readings landed between several chunks");
        bytes.Length.ShouldBe((int)size, "the size info gave is the size the read returned");
        (await SugaryServer.ParquetRowsAsync(bytes)).Count.ShouldBe(30);
    }

    [Fact]
    public async Task TheNextRead_SeesWhatLandedDuringTheLastOne()
    {
        await using var harness = await StartAsync();
        var server = harness.Server;
        var landed = false;
        await ReadOnAsync(server, [], async () =>
        {
            if (!landed)
            {
                landed = true;
                await server.SaveAsync(Reading(30, 300));
            }
        });

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var size = await SizeAsync(server);
        var bytes = await ReadOnAsync(server, []);

        bytes.Length.ShouldBe((int)size);
        (await SugaryServer.ParquetRowsAsync(bytes)).Count.ShouldBe(31);
    }

    // Two commands reading the same file at once: the second starts after a reading landed, while the
    // first is part way through. Each read is whole, the first the file it began on, the second the
    // newer one.
    [Fact]
    public async Task ASecondReadStartingMidway_LeavesTheFirstWhole()
    {
        await using var harness = await StartAsync();
        var server = harness.Server;
        List<byte> first = [];
        var opening = await BlobAsync(server, 0);
        first.AddRange(Convert.FromBase64String(opening["contentBase64"]!.GetValue<string>()));
        opening["eof"]!.GetValue<bool>().ShouldBeFalse();

        await server.SaveAsync(Reading(30, 300));
        harness.Clock.Advance(TimeSpan.FromSeconds(20));
        var second = await ReadOnAsync(server, []);
        var whole = await ReadOnAsync(server, first);

        (await SugaryServer.ParquetRowsAsync(whole)).Count.ShouldBe(30);
        (await SugaryServer.ParquetRowsAsync(second)).Count.ShouldBe(31);
    }

    // What was saved a moment ago is in the very next read: a finished read's version serves nobody else.
    [Fact]
    public async Task AReadRightAfterAFinishedOne_SeesWhatWasJustSaved()
    {
        await using var harness = await StartAsync();
        var server = harness.Server;
        await ReadOnAsync(server, []);

        await server.SaveAsync(Reading(30, 300));
        var bytes = await ReadOnAsync(server, []);

        (await SugaryServer.ParquetRowsAsync(bytes)).Count.ShouldBe(31);
    }

    // A read that stops part way and comes back after its version is gone cannot be finished from
    // another: it is told to start again, which is worth doing.
    [Fact]
    public async Task AReadComingBackAfterItsVersionIsGone_IsToldToStartAgain()
    {
        await using var harness = await StartAsync();
        var server = harness.Server;
        var opening = await BlobAsync(server, 0);
        var got = Convert.FromBase64String(opening["contentBase64"]!.GetValue<string>()).Length;

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var rest = await BlobAsync(server, got);

        rest["ok"]!.GetValue<bool>().ShouldBeFalse();
        rest["errorCode"]!.GetValue<string>().ShouldBe("transient_dependency");
        rest["retryable"]!.GetValue<bool>().ShouldBeTrue();
        rest["message"]!.GetValue<string>().ShouldContain("from the start");
    }
}