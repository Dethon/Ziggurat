using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// Transcription for the channels people type into shows up on the same dashboard the satellites
// already do. No new metric family: the existing voice speech-to-text members are recorded from
// the new call sites too, and the channel dimension is what separates the three.
public class TelegramBotServiceVoiceNoteMetricsTests : IDisposable
{
    private readonly TelegramPollingHarness _harness = new();

    [Fact]
    public async Task ADictation_RecordsItsLatencyAndATranscribedUtterance()
    {
        _harness.Transcriber.Result = new() { Text = "hola", AvgLogProb = -0.2, NoSpeechProb = 0.1 };

        await DriveAsync();

        var latency = Published(VoiceMetric.SttLatencyMs).ShouldHaveSingleItem();
        latency.DurationMs.ShouldNotBeNull();
        latency.Channel.ShouldBe("telegram");

        var transcribed = Published(VoiceMetric.UtteranceTranscribed).ShouldHaveSingleItem();
        transcribed.Channel.ShouldBe("telegram");
        transcribed.AvgLogProb.ShouldBe(-0.2);
    }

    [Fact]
    public async Task AFailedTranscription_RecordsTheErrorMember()
    {
        _harness.Transcriber.Fails = new TimeoutException("Lemonade did not answer");

        await DriveAsync();

        var error = Published(VoiceMetric.SttError).ShouldHaveSingleItem();
        error.Channel.ShouldBe("telegram");
        error.Error.ShouldNotBeNullOrWhiteSpace();
        Published(VoiceMetric.UtteranceTranscribed).ShouldBeEmpty();
    }

    // A recording the gate threw away is still a transcription that happened, and telling the two
    // apart is what the outcome label is for.
    [Fact]
    public async Task ARejectedTranscript_IsRecordedAsSuchRatherThanNotAtAll()
    {
        _harness.Transcriber.Result = new() { Text = "gracias por ver el video", AvgLogProb = -1.4 };

        await DriveAsync();

        Published(VoiceMetric.UtteranceTranscribed).ShouldHaveSingleItem().Outcome.ShouldBe("rejected");
    }

    public void Dispose() => _harness.Dispose();

    private IReadOnlyList<VoiceEvent> Published(VoiceMetric metric) =>
        [.. _harness.Metrics.Published.OfType<VoiceEvent>().Where(e => e.Metric == metric)];

    private async Task DriveAsync()
    {
        var message = TelegramPollingHarness.MediaMessage(threadId: 42);
        message.Voice = TelegramPollingHarness.VoiceNote();
        _harness.GivenTelegramHolds("voice-1", TelegramPollingHarness.OggOpusFixture, "voice/file_1.oga");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();
    }
}