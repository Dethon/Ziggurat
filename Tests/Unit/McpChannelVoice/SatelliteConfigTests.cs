using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteConfigTests
{
    [Theory]
    [InlineData(null, "Kitchen")]
    [InlineData("   ", "Kitchen")]
    [InlineData("Madrid, Spain", "Kitchen (Madrid, Spain)")]
    public void DisplayLocation_AppendsLocalityWhenPresent(string? locality, string expected)
    {
        var config = new SatelliteConfig { Identity = "household", Room = "Kitchen", Locality = locality };

        config.DisplayLocation.ShouldBe(expected);
    }

    [Fact]
    public void ResolveGateThresholds_NoOverride_UsesGlobals()
    {
        var config = new SatelliteConfig { Identity = "household", Room = "Kitchen" };
        var global = new WyomingClientSettings { SilenceRmsThreshold = 700, MinSpeechMs = 300 };

        config.ResolveRmsThreshold(global).ShouldBe(700);
        config.ResolveMinSpeechMs(global).ShouldBe(300);
    }

    [Fact]
    public void ResolveGateThresholds_WithOverride_UsesSatelliteValues()
    {
        // Mic front-ends differ (e.g. XVF3800 AGC raises the noise floor), so one global RMS
        // bar can't fit every satellite.
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Gate = new GateSettings { SilenceRmsThreshold = 900, MinSpeechMs = 400 }
        };
        var global = new WyomingClientSettings { SilenceRmsThreshold = 700, MinSpeechMs = 300 };

        config.ResolveRmsThreshold(global).ShouldBe(900);
        config.ResolveMinSpeechMs(global).ShouldBe(400);
    }

    [Fact]
    public void ResolveGateThresholds_PartialOverride_FallsBackPerField()
    {
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Gate = new GateSettings { SilenceRmsThreshold = 900 }
        };
        var global = new WyomingClientSettings { SilenceRmsThreshold = 700, MinSpeechMs = 300 };

        config.ResolveRmsThreshold(global).ShouldBe(900);
        config.ResolveMinSpeechMs(global).ShouldBe(300);
    }

    [Fact]
    public void ResolveAdaptiveGateKnobs_WithOverrides_PreferSatelliteValues()
    {
        var global = new WyomingClientSettings();
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Office",
            Gate = new GateSettings
            {
                FloorWindowMs = 5000,
                EnterMarginDb = 12,
                ExitMarginDb = 6,
                PeakDropDb = 20
            }
        };

        config.ResolveFloorWindowMs(global).ShouldBe(5000);
        config.ResolveEnterMarginDb(global).ShouldBe(12);
        config.ResolveExitMarginDb(global).ShouldBe(6);
        config.ResolvePeakDropDb(global).ShouldBe(20);
    }

    [Fact]
    public void ResolveAdaptiveGateKnobs_WithoutOverrides_FallBackToGlobal()
    {
        var global = new WyomingClientSettings();
        var config = new SatelliteConfig { Identity = "household", Room = "Office" };

        config.ResolveFloorWindowMs(global).ShouldBe(3000);
        config.ResolveEnterMarginDb(global).ShouldBe(9);
        config.ResolveExitMarginDb(global).ShouldBe(4);
        config.ResolvePeakDropDb(global).ShouldBe(10);
    }

    [Fact]
    public void ResolveIdentify_WithOverrides_PreferSatelliteValues()
    {
        var global = new SpeakerVerificationSettings();
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Office",
            Verification = new VerificationOverrides { IdentifyThreshold = 0.8, IdentifyMargin = 0.2 }
        };

        config.ResolveIdentifyThreshold(global).ShouldBe(0.8);
        config.ResolveIdentifyMargin(global).ShouldBe(0.2);
    }
}