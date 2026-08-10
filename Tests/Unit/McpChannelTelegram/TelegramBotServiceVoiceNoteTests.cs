using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// Someone holds the microphone in Telegram, speaks and sends. Nothing marks the turn as spoken:
// the agent sees an ordinary turn, exactly as the satellites' transcript dispatcher already
// produces. The audio is never a message — no attachment reference is minted and no bytes are kept.
public class TelegramBotServiceVoiceNoteTests : IDisposable
{
    private readonly TelegramPollingHarness _harness = new();

    [Fact]
    public async Task AVoiceNote_BecomesATurnWhoseContentIsTheTranscript()
    {
        _harness.Transcriber.Result = new() { Text = "pon el temporizador de diez minutos" };

        await DriveAsync();

        var notification = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Content.ShouldBe("pon el temporizador de diez minutos");
        _harness.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task AVoiceNote_ProducesNoAttachmentReference()
    {
        _harness.Transcriber.Result = new() { Text = "hola" };

        await DriveAsync();

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Attachments.ShouldBeNull();
    }

    [Fact]
    public async Task ADictatedTurn_CarriesTheSenderAndConversationATypedOneWould()
    {
        _harness.Transcriber.Result = new() { Text = "hola" };

        await DriveAsync();

        var notification = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Sender.ShouldBe("alice");
        notification.AgentId.ShouldBe(TelegramPollingHarness.AgentId);
        notification.ConversationId.ShouldBe("100:42");
    }

    // Ogg/Opus is what Telegram sends and what whisper refuses, so what leaves this channel is
    // always 16 kHz mono s16le WAV.
    [Fact]
    public async Task TheAudioReachesTheTranscriberAsSixteenKilohertzMonoWav()
    {
        _harness.Transcriber.Result = new() { Text = "hola" };

        await DriveAsync();

        var request = _harness.Transcriber.Requests.ShouldHaveSingleItem();
        request.MediaType.ShouldBe("audio/wav");
        var wav = request.Audio.ToArray();
        System.Text.Encoding.ASCII.GetString(wav[..4]).ShouldBe("RIFF");
        BitConverter.ToInt16(wav, 22).ShouldBe((short)1);   // mono
        BitConverter.ToInt32(wav, 24).ShouldBe(16_000);
        BitConverter.ToInt16(wav, 34).ShouldBe((short)16);  // s16le
    }

    // An audio file is a file someone attached, not a microphone they held: today's refusal stands.
    [Fact]
    public async Task AnAudioFile_StillGetsTodaysRefusalAndIsNeverTranscribed()
    {
        var message = TelegramPollingHarness.MediaMessage(threadId: 42);
        message.Audio = new Audio
        {
            FileId = "audio-1",
            FileUniqueId = "u-audio",
            Duration = 30,
            FileName = "song.mp3",
            MimeType = "audio/mpeg"
        };

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("song.mp3");
        _harness.Transcriber.Requests.ShouldBeEmpty();
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AVideoNote_IsStillDroppedWithoutATranscription()
    {
        var message = TelegramPollingHarness.MediaMessage(threadId: 42);
        message.VideoNote = new VideoNote { FileId = "note-1", FileUniqueId = "u-note", Length = 240, Duration = 4 };

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        _harness.Transcriber.Requests.ShouldBeEmpty();
        _harness.Sent.ShouldBeEmpty();
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    public void Dispose() => _harness.Dispose();

    // A voice note carries no caption of its own, so a forum thread is what addresses one to the
    // bot — the same rule every other media message has always been under.
    private async Task DriveAsync(string? caption = null, int durationSeconds = 2)
    {
        var message = TelegramPollingHarness.MediaMessage(caption: caption, threadId: 42);
        message.Voice = TelegramPollingHarness.VoiceNote(durationSeconds: durationSeconds);
        _harness.GivenTelegramHolds("voice-1", TelegramPollingHarness.OggOpusFixture, "voice/file_1.oga");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();
    }
}