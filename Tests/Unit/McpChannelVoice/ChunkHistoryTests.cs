using McpChannelVoice.Services.WyomingProtocol;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ChunkHistoryTests
{
    [Fact]
    public void Record_WithinSpan_SnapshotReturnsAllSamples()
    {
        var time = new FakeTimeProvider();
        var history = new ChunkHistory(time, TimeSpan.FromSeconds(2));

        history.Record(100, false);
        time.Advance(TimeSpan.FromMilliseconds(80));
        history.Record(900, true);

        var samples = history.Snapshot();
        samples.Count.ShouldBe(2);
        samples[0].Rms.ShouldBe(100);
        samples[0].IsSpeech.ShouldBeFalse();
        samples[1].IsSpeech.ShouldBeTrue();
        samples[1].Timestamp.ShouldBeGreaterThan(samples[0].Timestamp);
    }

    [Fact]
    public void Record_BeyondSpan_EvictsOldSamples()
    {
        var time = new FakeTimeProvider();
        var history = new ChunkHistory(time, TimeSpan.FromMilliseconds(500));

        history.Record(1, false);
        time.Advance(TimeSpan.FromMilliseconds(600));
        history.Record(2, true);

        var samples = history.Snapshot();
        samples.Count.ShouldBe(1);
        samples[0].Rms.ShouldBe(2);
    }
}