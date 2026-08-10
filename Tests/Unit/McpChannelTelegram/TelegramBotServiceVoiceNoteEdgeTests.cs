using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// The cases around the happy path, so nobody is left guessing. Every one of them is asserted on
// what the person sees: the turn that reached the channel inbox, or the message the bot sent back.
public class TelegramBotServiceVoiceNoteEdgeTests : IDisposable
{
    private readonly TelegramPollingHarness _harness = new();

    [Fact]
    public async Task ACaptionedVoiceNote_KeepsBothWithTheCaptionFirst()
    {
        _harness.Transcriber.Result = new() { Text = "y trae el pan" };

        await DriveAsync(caption: "/ask recuerda esto");

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Content
            .ShouldBe("/ask recuerda esto\ny trae el pan");
    }

    // Read off the update, so a recording too long to be worth transcribing is refused while the
    // person is still standing there rather than after a download that was never going to be used.
    [Fact]
    public async Task AVoiceNoteLongerThanTheCap_IsRefusedBeforeItsBytesAreDownloaded()
    {
        await DriveAsync(durationSeconds: 121);

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("too long");
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
        _harness.BotClient.Verify(
            b => b.SendRequest(It.IsAny<GetFileRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _harness.Transcriber.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AVoiceNoteExactlyAtTheCap_IsStillTranscribed()
    {
        _harness.Transcriber.Result = new() { Text = "hola" };

        await DriveAsync(durationSeconds: 120);

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Content.ShouldBe("hola");
    }

    [Fact]
    public async Task AnEmptyTranscript_SaysSoInsteadOfOpeningATurn()
    {
        _harness.Transcriber.Result = new() { Text = "   " };

        await DriveAsync();

        await ShouldHaveSaidItCouldNotMakeItOutAsync();
    }

    [Fact]
    public async Task ATranscriptBelowTheAverageLogProbabilityFloor_SaysSoInsteadOfOpeningATurn()
    {
        _harness.Transcriber.Result = new() { Text = "gracias por ver el video", AvgLogProb = -1.4 };

        await DriveAsync();

        await ShouldHaveSaidItCouldNotMakeItOutAsync();
    }

    [Fact]
    public async Task ATranscriptWhoseNoSpeechProbabilityIsTooHigh_SaysSoInsteadOfOpeningATurn()
    {
        _harness.Transcriber.Result = new() { Text = "subtitulos por la comunidad", NoSpeechProb = 0.8 };

        await DriveAsync();

        await ShouldHaveSaidItCouldNotMakeItOutAsync();
    }

    // Lemonade emits no signals on a plain json body, and a missing signal is not a bad one.
    [Fact]
    public async Task ATranscriptWithNoConfidenceSignalsAtAll_FailsOpen()
    {
        _harness.Transcriber.Result = new() { Text = "hola" };

        await DriveAsync();

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Content.ShouldBe("hola");
    }

    [Fact]
    public async Task ATranscriberFailure_SaysSoInsteadOfOpeningATurn()
    {
        _harness.Transcriber.Fails = new TimeoutException("Lemonade did not answer");

        await DriveAsync();

        await ShouldHaveSaidItCouldNotMakeItOutAsync();
    }

    // A caption is not a consolation prize: an answer to half of what was said is worse than
    // saying the other half could not be made out.
    [Fact]
    public async Task ACaptionedVoiceNoteThatCouldNotBeUnderstood_OpensNoTurnEither()
    {
        _harness.Transcriber.Fails = new TimeoutException("Lemonade did not answer");

        await DriveAsync(caption: "/ask recuerda esto");

        await ShouldHaveSaidItCouldNotMakeItOutAsync();
    }

    [Fact]
    public async Task AVoiceNoteInAContainerNobodyRecognises_SaysSoInsteadOfOpeningATurn()
    {
        await DriveAsync(audio: [1, 2, 3, 4, 5, 6, 7, 8]);

        await ShouldHaveSaidItCouldNotMakeItOutAsync();
        _harness.Transcriber.Requests.ShouldBeEmpty();
    }

    // whisper decodes these itself, so decoding them here would only add a way to get it wrong.
    [Theory]
    [InlineData("voice-note.wav", "audio/wav")]
    [InlineData("voice-note.mp3", "audio/mpeg")]
    [InlineData("voice-note.flac", "audio/flac")]
    [InlineData("voice-note-vorbis.ogg", "audio/ogg")]
    public async Task AContainerWhisperReadsItself_IsForwardedByteForByte(string fixture, string mediaType)
    {
        _harness.Transcriber.Result = new() { Text = "hola" };
        var audio = TelegramPollingHarness.Fixture(fixture);

        await DriveAsync(audio: audio);

        var request = _harness.Transcriber.Requests.ShouldHaveSingleItem();
        request.MediaType.ShouldBe(mediaType);
        request.Audio.ToArray().ShouldBe(audio);
    }

    [Fact]
    public async Task AReportedMimeTypeThatDisagreesWithTheBytes_IsIgnored()
    {
        _harness.Transcriber.Result = new() { Text = "hola" };

        // Ogg/Opus bytes announced as an MP3: believing the sender would post Opus at whisper,
        // which answers 400 to every one of them.
        await DriveAsync(mimeType: "audio/mpeg");

        _harness.Transcriber.Requests.ShouldHaveSingleItem().MediaType.ShouldBe("audio/wav");
    }

    public void Dispose() => _harness.Dispose();

    private async Task ShouldHaveSaidItCouldNotMakeItOutAsync()
    {
        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("could not make out");
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    private async Task DriveAsync(
        string? caption = null,
        int durationSeconds = 2,
        string? mimeType = "audio/ogg",
        byte[]? audio = null)
    {
        var message = TelegramPollingHarness.MediaMessage(caption: caption, threadId: 42);
        message.Voice = TelegramPollingHarness.VoiceNote(durationSeconds: durationSeconds, mimeType: mimeType);
        _harness.GivenTelegramHolds(
            "voice-1", audio ?? TelegramPollingHarness.OggOpusFixture, "voice/file_1.oga");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();
    }
}