using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceDeliveryRegistryTests
{
    private static VoiceDeliveryRegistry Build(
        FakeTimeProvider clock, TimeSpan? lifetime = null, ReplyTextAccumulator? accumulator = null) =>
        new(clock, lifetime ?? TimeSpan.FromMinutes(5),
            accumulator ?? new ReplyTextAccumulator(), NullLogger<VoiceDeliveryRegistry>.Instance);

    [Fact]
    public void Bind_ThenResolve_ReturnsTarget()
    {
        var sut = Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var target = new AnnounceTarget { SatelliteId = "office-01" };

        sut.Bind("c1", target);

        sut.Resolve("c1").ShouldBe(target);
    }

    [Fact]
    public void Remove_DropsBinding()
    {
        var sut = Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        sut.Bind("c1", new AnnounceTarget { All = true });

        sut.Remove("c1");

        sut.Resolve("c1").ShouldBeNull();
    }

    [Fact]
    public void Binding_ExpiresAfterIdleLifetime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = Build(clock, TimeSpan.FromMinutes(5));
        sut.Bind("c1", new AnnounceTarget { SatelliteId = "office-01" });

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        sut.Resolve("c1").ShouldBeNull();
    }

    [Fact]
    public void Expire_FlushesStrandedAccumulatorEntry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var accumulator = new ReplyTextAccumulator();
        var sut = Build(clock, TimeSpan.FromMinutes(5), accumulator);
        sut.Bind("c1", new AnnounceTarget { SatelliteId = "office-01" });
        accumulator.Append("c1", "stranded reply");

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        // Expire must drop the buffered text so an abandoned scheduled delivery doesn't leak it.
        accumulator.Flush("c1").ShouldBeEmpty();
    }
}