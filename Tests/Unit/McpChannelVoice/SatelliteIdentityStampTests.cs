using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// Which satellite a voice event is about used to be written by hand at twenty call sites, so a
// report could name two of the three fields and forget the last. One stamp owns the triple now.
public class SatelliteIdentityStampTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Room = "Kitchen", Identity = "household" });

    [Fact]
    public void About_StampsAllThreeIdentityFieldsOffTheSession()
    {
        var stamped = new VoiceEvent { Metric = VoiceMetric.SttLatencyMs }.About(Session());

        stamped.SatelliteId.ShouldBe("kitchen-01");
        stamped.Room.ShouldBe("Kitchen");
        stamped.Identity.ShouldBe("household");
    }

    // An offline target has no session to be named by, only its id and whatever the registry knows
    // about it — the path that used to write the three fields by hand at every announce and alarm
    // site, which is exactly where one of them goes missing.
    [Fact]
    public void Of_NamesAnOfflineSatelliteByItsIdAndConfig()
    {
        var identity = SatelliteIdentity.Of(
            "kitchen-01", new SatelliteConfig { Room = "Kitchen", Identity = "household" });

        var stamped = new VoiceEvent { Metric = VoiceMetric.AlarmOffline }.About(identity);

        stamped.SatelliteId.ShouldBe("kitchen-01");
        stamped.Room.ShouldBe("Kitchen");
        stamped.Identity.ShouldBe("household");
    }

    [Fact]
    public void Of_UnconfiguredSatellite_NamesItByIdAlone()
    {
        var stamped = new VoiceEvent { Metric = VoiceMetric.AlarmOffline }
            .About(SatelliteIdentity.Of("ghost-01", null));

        stamped.SatelliteId.ShouldBe("ghost-01");
        stamped.Room.ShouldBeNull();
        stamped.Identity.ShouldBeNull();
    }
}