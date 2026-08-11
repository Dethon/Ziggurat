using Domain.DTOs.Voice;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Verification;

public class SpeakerVerifierTests
{
    private sealed class FixedEmbedder(float[] embedding) : ISpeakerEmbedder
    {
        public float[] Embed(ReadOnlySpan<byte> pcmS16Le) => embedding;
    }

    private static readonly float[] _franVoice = OnnxSpeakerEmbedder.L2Normalize([1f, 0f, 0f]);
    private static readonly float[] _tvVoice = OnnxSpeakerEmbedder.L2Normalize([0f, 1f, 0f]);

    private static SatelliteConfig Config(VerificationOverrides? overrides = null) =>
        new() { Identity = "household", Room = "office", Verification = overrides };

    private static IReadOnlyList<AudioChunk> Chunks() =>
        [new AudioChunk { Data = new byte[3200], Format = AudioFormat.WyomingStandard }];

    private static SpeakerVerifier Verifier(
        float[] heardVoice,
        SpeakerVerificationSettings? settings = null,
        IReadOnlyList<SpeakerProfile>? profiles = null) =>
        new(
            settings ?? new SpeakerVerificationSettings { Enabled = true },
            () => (new FixedEmbedder(heardVoice), profiles ?? [new SpeakerProfile("fran", [_franVoice])]),
            NullLogger<SpeakerVerifier>.Instance);

    [Fact]
    public async Task VerifyAsync_EnrolledVoice_Accepts()
    {
        var result = await Verifier(_franVoice).VerifyAsync(Chunks(), 2000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Accepted);
        result.Similarity!.Value.ShouldBe(1.0, 1e-5);
        result.BestMatch.ShouldBe("fran");
    }

    [Fact]
    public async Task VerifyAsync_UnknownVoice_Rejects()
    {
        var result = await Verifier(_tvVoice).VerifyAsync(Chunks(), 2000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Rejected);
        result.Similarity!.Value.ShouldBe(0.0, 1e-5);
    }

    [Fact]
    public async Task VerifyAsync_ShortSpeech_SkipsWithoutEmbedding()
    {
        var result = await Verifier(_tvVoice).VerifyAsync(Chunks(), 500, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Skipped);
        result.Similarity.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyAsync_ShortSpeech_MinSpeechNotEnforced_StillVerifies()
    {
        // The early-close check judges a still-running capture on its continuous audio, so it opts
        // out of the short-utterance skip: sub-MinVerifySpeechMs speech must still be verified.
        var result = await Verifier(_tvVoice).VerifyAsync(Chunks(), 500, Config(), default, enforceMinSpeech: false);

        result.Decision.ShouldBe(SpeakerDecision.Rejected);
        result.Similarity!.Value.ShouldBe(0.0, 1e-5);
    }

    [Fact]
    public async Task VerifyAsync_DisabledGlobally_Skips()
    {
        var verifier = Verifier(_tvVoice, new SpeakerVerificationSettings { Enabled = false });

        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Skipped);
    }

    [Fact]
    public async Task VerifyAsync_PerSatelliteDisable_OverridesGlobalEnable()
    {
        var config = Config(new VerificationOverrides { Enabled = false });

        (await Verifier(_tvVoice).VerifyAsync(Chunks(), 2000, config, default))
            .Decision.ShouldBe(SpeakerDecision.Skipped);
    }

    [Fact]
    public async Task VerifyAsync_PerSatelliteThreshold_Overrides()
    {
        // Similarity 0.0 vs a per-satellite threshold of -1 => accepted.
        var config = Config(new VerificationOverrides { SimilarityThreshold = -1 });

        (await Verifier(_tvVoice).VerifyAsync(Chunks(), 2000, config, default))
            .Decision.ShouldBe(SpeakerDecision.Accepted);
    }

    [Fact]
    public async Task VerifyAsync_NoProfiles_IsUnavailable()
    {
        var verifier = Verifier(_tvVoice, profiles: []);

        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Unavailable);
    }

    [Fact]
    public async Task VerifyAsync_BackendFactoryThrows_IsUnavailableAndDoesNotRetry()
    {
        var calls = 0;
        var verifier = new SpeakerVerifier(
            new SpeakerVerificationSettings { Enabled = true },
            () => { calls++; throw new InvalidOperationException("model missing"); },
            NullLogger<SpeakerVerifier>.Instance);

        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Unavailable);
        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Unavailable);
        calls.ShouldBe(1); // fail-open once, never re-tried per capture
    }

    [Fact]
    public async Task VerifyAsync_EmbeddingThrows_IsUnavailable()
    {
        var throwing = new ThrowingEmbedder();
        var verifier = new SpeakerVerifier(
            new SpeakerVerificationSettings { Enabled = true },
            () => ((ISpeakerEmbedder)throwing, [new SpeakerProfile("fran", [_franVoice])]),
            NullLogger<SpeakerVerifier>.Instance);

        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Unavailable);
    }

    private sealed class ThrowingEmbedder : ISpeakerEmbedder
    {
        public float[] Embed(ReadOnlySpan<byte> pcmS16Le) => throw new InvalidOperationException("boom");
    }

    private static float[] Unit(params float[] v) => OnnxSpeakerEmbedder.L2Normalize(v);

    private static SpeakerVerifier VerifierWith(
        float[] heardVoice,
        IReadOnlyList<SpeakerProfile> profiles,
        double similarityThreshold = 0.45,
        double identifyThreshold = 0.65,
        double identifyMargin = 0.10,
        double shortSpeechSimilarityThreshold = 0.50,
        double shortSpeechIdentifyThreshold = 0.65,
        int fullThresholdSpeechMs = 4000) =>
        new(
            new SpeakerVerificationSettings
            {
                Enabled = true,
                SimilarityThreshold = similarityThreshold,
                IdentifyThreshold = identifyThreshold,
                IdentifyMargin = identifyMargin,
                ShortSpeechSimilarityThreshold = shortSpeechSimilarityThreshold,
                ShortSpeechIdentifyThreshold = shortSpeechIdentifyThreshold,
                FullThresholdSpeechMs = fullThresholdSpeechMs
            },
            () => (new FixedEmbedder(heardVoice), profiles),
            NullLogger<SpeakerVerifier>.Instance);

    [Fact]
    public async Task VerifyAsync_AcceptedButBelowIdentifyThreshold_DoesNotIdentify()
    {
        // Passes the gate (>= 0.45) but sits in the doubtful band (< 0.65) -> household, not named.
        var heard = Unit(0.55f, (float)Math.Sqrt(1 - (0.55 * 0.55)), 0f); // cosine 0.55 to fran
        var result = await VerifierWith(heard, [new SpeakerProfile("fran", [_franVoice])])
            .VerifyAsync(Chunks(), 2000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Accepted);
        result.Similarity!.Value.ShouldBe(0.55, 1e-3);
        result.IdentifiedSpeaker.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyAsync_AboveIdentifyThresholdButThinMargin_DoesNotIdentify()
    {
        // Two enrolled voices score close together: naming the top one would be a guess, so the
        // margin guard withholds the identity even though the top score clears IdentifyThreshold.
        var heard = Unit(0.7f, 0.65f, 0f);
        var result = await VerifierWith(
                heard, [new SpeakerProfile("fran", [_franVoice]), new SpeakerProfile("bob", [_tvVoice])])
            .VerifyAsync(Chunks(), 2000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Accepted);
        result.BestMatch.ShouldBe("fran");
        result.IdentifiedSpeaker.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyAsync_AboveIdentifyThresholdWithMargin_TwoProfiles_Identifies()
    {
        // Best (1.0) clears the runner-up (0.0) by well over the margin -> named.
        var result = await VerifierWith(
                _franVoice, [new SpeakerProfile("fran", [_franVoice]), new SpeakerProfile("bob", [_tvVoice])])
            .VerifyAsync(Chunks(), 2000, Config(), default);

        result.IdentifiedSpeaker.ShouldBe("fran");
    }

    [Fact]
    public async Task VerifyAsync_MultiPrototypeProfile_ScoresBestPrototypeNotMean()
    {
        // Off-axis enrollment take orthogonal to the on-axis one: speech matching the off-axis
        // prototype must score as that prototype (1.0), not as the diluted mean (~0.71).
        var offAxis = Unit(0f, 0f, 1f);
        var result = await VerifierWith(offAxis, [new SpeakerProfile("fran", [_franVoice, offAxis])])
            .VerifyAsync(Chunks(), 2000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Accepted);
        result.Similarity!.Value.ShouldBe(1.0, 1e-5);
        result.IdentifiedSpeaker.ShouldBe("fran");
    }

    [Fact]
    public async Task VerifyAsync_ShortSpeech_RelaxedThresholdAccepts()
    {
        // Short utterances embed unreliably (genuine speech can score ~0.5-0.6), so the accept
        // bar ramps down with verified speech length: at 1000ms the effective bar sits near the
        // short floor (~0.51 for 0.50→0.70 over 800→4000ms), admitting a 0.55 match that the
        // full-speech 0.70 bar would reject.
        var heard = Unit(0.55f, (float)Math.Sqrt(1 - (0.55 * 0.55)), 0f);
        var result = await VerifierWith(heard, [new SpeakerProfile("fran", [_franVoice])], similarityThreshold: 0.70)
            .VerifyAsync(Chunks(), 1000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Accepted);
    }

    [Fact]
    public async Task VerifyAsync_LongSpeech_FullThresholdRejects()
    {
        // At/after FullThresholdSpeechMs the ramp is over: a long capture faces the full bar.
        var heard = Unit(0.65f, (float)Math.Sqrt(1 - (0.65 * 0.65)), 0f);
        var result = await VerifierWith(heard, [new SpeakerProfile("fran", [_franVoice])], similarityThreshold: 0.70)
            .VerifyAsync(Chunks(), 5000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Rejected);
    }

    [Fact]
    public async Task VerifyAsync_MidRamp_InterpolatesThreshold()
    {
        // Halfway along the 800→4000ms ramp (2400ms) the bar is exactly midway 0.50→0.70 = 0.60.
        var heard = Unit(0.61f, (float)Math.Sqrt(1 - (0.61 * 0.61)), 0f);
        var result = await VerifierWith(heard, [new SpeakerProfile("fran", [_franVoice])], similarityThreshold: 0.70)
            .VerifyAsync(Chunks(), 2400, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Accepted);

        var below = Unit(0.59f, (float)Math.Sqrt(1 - (0.59 * 0.59)), 0f);
        var rejected = await VerifierWith(below, [new SpeakerProfile("fran", [_franVoice])], similarityThreshold: 0.70)
            .VerifyAsync(Chunks(), 2400, Config(), default);

        rejected.Decision.ShouldBe(SpeakerDecision.Rejected);
    }

    [Fact]
    public async Task VerifyAsync_ShortSpeech_RelaxedIdentifyBarNamesSpeaker()
    {
        // The naming bar ramps with speech length like the accept bar: at 1000ms the effective
        // identify bar is ~0.656 (0.65→0.75 over 800→4000ms), so a 0.70 match names the speaker
        // that the flat full-speech 0.75 would have left as household.
        var heard = Unit(0.70f, (float)Math.Sqrt(1 - (0.70 * 0.70)), 0f);
        var result = await VerifierWith(
                heard, [new SpeakerProfile("fran", [_franVoice])],
                similarityThreshold: 0.70, identifyThreshold: 0.75)
            .VerifyAsync(Chunks(), 1000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Accepted);
        result.IdentifiedSpeaker.ShouldBe("fran");
    }

    [Fact]
    public async Task VerifyAsync_LongSpeech_FullIdentifyBarWithholdsName()
    {
        // At/after FullThresholdSpeechMs naming faces the full identify bar: a long capture at
        // 0.72 is accepted but stays household (TV-length blends must never be attributed).
        var heard = Unit(0.72f, (float)Math.Sqrt(1 - (0.72 * 0.72)), 0f);
        var result = await VerifierWith(
                heard, [new SpeakerProfile("fran", [_franVoice])],
                similarityThreshold: 0.70, identifyThreshold: 0.75)
            .VerifyAsync(Chunks(), 5000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Accepted);
        result.IdentifiedSpeaker.ShouldBeNull();
    }
}