using McpChannelVoice.Services;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TranscriptionOptionsFactoryTests
{
    private static readonly CaptureStats _stats = new(PeakRms: 2000, FloorRms: 512.5, SpeechMs: 1500, EndReason: "ended");

    private static SatelliteConfig MakeConfig(SttOverrides? stt = null) =>
        new() { Identity = "household", Room = "Kitchen", Stt = stt };

    [Fact]
    public void ConclusiveIdentityWins()
    {
        var v = new SpeakerVerification(SpeakerDecision.Accepted, 0.81, BestMatch: "Tradaly", IdentifiedSpeaker: "Dethon");
        var options = TranscriptionOptionsFactory.Create("office-01", MakeConfig(), v, _stats);
        options.TargetSpeaker.ShouldBe("Dethon");
        options.NoiseFloorRms.ShouldBe(512.5);
    }

    [Fact]
    public void AcceptedButAmbiguousFallsBackToBestMatch()
    {
        var v = new SpeakerVerification(SpeakerDecision.Accepted, 0.66, BestMatch: "Tradaly", IdentifiedSpeaker: null);
        TranscriptionOptionsFactory.Create("office-01", MakeConfig(), v, _stats).TargetSpeaker.ShouldBe("Tradaly");
    }

    [Theory]
    [InlineData(SpeakerDecision.Rejected)]
    [InlineData(SpeakerDecision.Skipped)]
    [InlineData(SpeakerDecision.Unavailable)]
    public void NonAcceptedDecisionsYieldNoTarget(SpeakerDecision decision)
    {
        var v = new SpeakerVerification(decision, BestMatch: "Dethon", IdentifiedSpeaker: "Dethon");
        TranscriptionOptionsFactory.Create("office-01", MakeConfig(), v, _stats).TargetSpeaker.ShouldBeNull();
    }

    [Fact]
    public void NoVerifierMeansNoTargetButFloorStillFlows()
    {
        var options = TranscriptionOptionsFactory.Create("office-01", MakeConfig(), verification: null, _stats);
        options.TargetSpeaker.ShouldBeNull();
        options.NoiseFloorRms.ShouldBe(512.5);
    }

    [Fact]
    public void LanguageFlowsThroughFromConfig()
    {
        var config = MakeConfig(new SttOverrides { OpenAi = new OpenAiSttOverrides { Language = "es" } });
        var options = TranscriptionOptionsFactory.Create("office-01", config, verification: null, _stats);
        options.Language.ShouldBe("es");
    }
}