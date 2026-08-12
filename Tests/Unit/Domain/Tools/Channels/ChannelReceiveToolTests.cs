using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using Domain.Tools.Channels;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Channels;

public class ChannelReceiveToolTests
{
    // Pins the clamp structurally: an unclamped maxWaitMs beyond ChannelProtocol.DefaultReceiveWaitMs
    // would park a subscriber's poll past ChannelProtocol.LiveSubscriberFreshness (sized for one
    // fully held poll plus one retry backoff), making a genuinely live subscriber read as dead.
    // Advancing time just past the clamp ceiling (but nowhere near the caller-requested wait) must
    // resolve the call with an empty batch — if the clamp weren't applied, the call would still be
    // parked waiting for the full 90s.
    [Fact]
    public async Task Run_MaxWaitBeyondDefaultCeiling_IsClampedAndTimesOutEarly()
    {
        var time = new ArmedClock();
        var inbox = new ChannelInbox(time);
        var tool = new TestableChannelReceiveTool(inbox);

        var call = tool.TestRun("sess-1", 90_000, CancellationToken.None);

        // The clamped wait is the due time the parked call arms, so waiting for that timer proves
        // the clamp was applied and makes the advance land on something that exists. Advancing
        // first fires nothing and leaves the call parked for the full ninety seconds.
        await time.AdvancePastAsync(TimeSpan.FromMilliseconds(ChannelProtocol.DefaultReceiveWaitMs));

        var json = await call.WaitAsync(TimeSpan.FromSeconds(5));
        var result = JsonSerializer.Deserialize<ChannelReceiveResult>(json, ChannelProtocol.SerializerOptions)!;

        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_NegativeMaxWait_ClampsToZeroAndReturnsImmediately()
    {
        var inbox = new ChannelInbox();
        var tool = new TestableChannelReceiveTool(inbox);

        var json = await tool.TestRun("sess-1", -1, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var result = JsonSerializer.Deserialize<ChannelReceiveResult>(json, ChannelProtocol.SerializerOptions)!;

        result.Items.ShouldBeEmpty();
    }

    private sealed class TestableChannelReceiveTool(ChannelInbox inbox) : ChannelReceiveTool(inbox)
    {
        public Task<string> TestRun(string subscriberId, int maxWaitMs, CancellationToken ct) =>
            Run(subscriberId, maxWaitMs, ct);
    }
}