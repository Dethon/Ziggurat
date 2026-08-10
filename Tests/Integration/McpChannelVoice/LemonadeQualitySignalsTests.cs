using Domain.DTOs.Voice;
using Infrastructure.Clients.Transcription;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.McpChannelVoice;

// Pins the real lemonade container's transcription contract, end to end through both endpoints:
// Kokoro synthesizes a Spanish phrase, whisper transcribes it back, and the verbose_json body
// must carry the avg_logprob / no_speech_prob signals the gibberish gate thresholds — the unit
// suite stubs these, so only this test proves the deployed server actually emits them.
// LemonadeFixture spins the container (CPU tier) over the provisioned model cache; when Docker,
// the image, or that cache is unavailable it records a SkipReason and the test skips (never
// hard-fails) — the External-category convention. First decode loads the whisper model on CPU, hence
// the generous timeout.
[Trait("Category", "External")]
[Collection(LemonadeCollection.Name)]
public class LemonadeQualitySignalsTests(LemonadeFixture fixture, ITestOutputHelper output)
{
    private sealed class PassthroughFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = Timeout.InfiniteTimeSpan };
    }

    [SkippableFact]
    public async Task TranscribeAsync_RealLemonade_EmitsGibberishGateSignals()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var factory = new PassthroughFactory();

        var tts = new OpenAiTextToSpeech(
            factory,
            new OpenAiTtsConfig { BaseUrl = fixture.BaseUrl },
            NullLogger<OpenAiTextToSpeech>.Instance);
        var sttConfig = new OpenAiSttConfig
        {
            BaseUrl = fixture.BaseUrl,
            Language = "es",
            RequestTimeout = TimeSpan.FromMinutes(10)
        };
        var stt = new OpenAiSpeechToText(
            new LemonadeTranscriptionClient(
                factory,
                sttConfig.ToTranscriptionClientConfig(),
                NullLogger<LemonadeTranscriptionClient>.Instance),
            sttConfig);

        var result = await stt.TranscribeAsync(
            tts.SynthesizeAsync("hola, ¿qué hora es?", new SynthesisOptions(), cts.Token),
            new TranscriptionOptions(),
            cts.Token);

        output.WriteLine(
            $"text=\"{result.Text}\" avg_logprob={result.AvgLogProb} no_speech_prob={result.NoSpeechProb}");

        result.Text.ShouldNotBeNullOrWhiteSpace();
        result.AvgLogProb.ShouldNotBeNull();
        result.NoSpeechProb.ShouldNotBeNull();
        result.AvgLogProb!.Value.ShouldBeLessThanOrEqualTo(0);
        result.NoSpeechProb!.Value.ShouldBeInRange(0, 1);

        // Calibration, not just contract: a clean Kokoro-synthesized phrase must clear the shipped
        // default gate, or the migration silently drops every real utterance in production. This is
        // the one check the fail-open null-guard cannot substitute for — mis-scaled (non-null)
        // signals would sail past the range asserts above while failing the gate.
        var defaults = new OpenAiSttConfig();
        result.AvgLogProb!.Value.ShouldBeGreaterThan(defaults.AvgLogProbThreshold);
        result.NoSpeechProb!.Value.ShouldBeLessThan(defaults.NoSpeechProbThreshold);
    }
}