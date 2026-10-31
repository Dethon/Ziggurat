using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Transcription;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Transcription;

// Every household's speech is heard by the shared Lemonade, so each transcription is counted in
// seconds of speech against the household it was for.
public class MeteredAudioTranscriberTests
{
    private static readonly AudioFormat _sixteenKhzMono = new() { SampleRateHz = 16_000, SampleWidthBytes = 2, Channels = 1 };

    [Fact]
    public async Task AWavRecording_CountsItsOwnLengthAgainstTheRequestsHousehold()
    {
        var metrics = new RecordingMetricsPublisher();
        var sut = new MeteredAudioTranscriber(new Answering(new TranscriptionResult { Text = "hola" }), metrics);

        await sut.TranscribeAsync(
            new TranscriptionRequest
            {
                Audio = WavAudio.FromPcm(new byte[48_000], _sixteenKhzMono),
                MediaType = WavAudio.MediaType,
                TenantId = "home"
            },
            CancellationToken.None);

        metrics.Published.OfType<LemonadeUsageEvent>().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Use.ShouldBe(LemonadeUse.Transcription),
            e => e.Amount.ShouldBe(1.5),
            e => e.TenantId.ShouldBe("home"));
    }

    // A container the client posts as it arrived has no header to read; whisper says how long it was.
    [Fact]
    public async Task ARecordingInAnotherContainer_CountsTheLengthWhisperReported()
    {
        var metrics = new RecordingMetricsPublisher();
        var sut = new MeteredAudioTranscriber(
            new Answering(new TranscriptionResult { Text = "hola", AudioSeconds = 4.25 }), metrics);

        await sut.TranscribeAsync(
            new TranscriptionRequest { Audio = new byte[] { 1, 2, 3 }, MediaType = "audio/mpeg", TenantId = "home" },
            CancellationToken.None);

        metrics.Published.OfType<LemonadeUsageEvent>().ShouldHaveSingleItem().Amount.ShouldBe(4.25);
    }

    // Nothing posted, nothing heard: the client answers an empty recording without asking Lemonade.
    [Fact]
    public async Task AnEmptyRecording_CountsNothing()
    {
        var metrics = new RecordingMetricsPublisher();
        var sut = new MeteredAudioTranscriber(new Answering(new TranscriptionResult { Text = "" }), metrics);

        await sut.TranscribeAsync(
            new TranscriptionRequest { Audio = ReadOnlyMemory<byte>.Empty, MediaType = WavAudio.MediaType },
            CancellationToken.None);

        metrics.Published.ShouldBeEmpty();
    }

    private sealed class Answering(TranscriptionResult result) : IAudioTranscriber
    {
        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct) =>
            Task.FromResult(result);
    }
}