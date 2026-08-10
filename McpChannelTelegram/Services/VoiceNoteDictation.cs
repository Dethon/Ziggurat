using Domain.Contracts;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Transcription;
using McpChannelTelegram.Settings;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace McpChannelTelegram.Services;

// What one voice note became: the words, or the short reply saying they could not be made out.
// Exactly one of the two is set.
public sealed record Dictation(string? Transcript, string? Refusal);

// One voice note turned into words: refused on its reported length before a byte is fetched,
// otherwise downloaded, decoded if whisper cannot read the container itself, and transcribed.
// Nothing is stored — no attachment reference is minted, and the bytes live only as long as the
// request.
//
// Every way this can fail ends in the same short reply rather than in silence, because being
// misheard and being ignored look identical from the other end.
public sealed class VoiceNoteDictation(
    IAudioTranscriber transcriber,
    DictationSettings settings,
    ILogger<VoiceNoteDictation> logger)
{
    // Said rather than nothing: being misheard and being ignored are indistinguishable from the
    // other end, and only one of them is worth recording again for.
    private const string CouldNotUnderstand = "I could not make out that voice note.";

    public async Task<Dictation> ReadAsync(ITelegramBotClient botClient, Voice voice, CancellationToken ct)
    {
        // Read off the update, so a recording too long to be worth transcribing is refused while
        // the person is still standing there rather than after a download nothing will use.
        if (voice.Duration > settings.MaxLength.TotalSeconds)
        {
            return new Dictation(null, TooLongReply());
        }

        try
        {
            var audio = await DownloadAsync(botClient, voice, ct);
            if (AudioContainer.Sniff(audio.Span) is not { } container)
            {
                logger.LogInformation("A voice note arrived in a container nothing here recognises");
                return new Dictation(null, CouldNotUnderstand);
            }

            var result = await transcriber.TranscribeAsync(
                Prepare(audio, container) with { Language = settings.Transcription.Language }, ct);

            return IsWorthATurn(result)
                ? new Dictation(result.Text.Trim(), null)
                : new Dictation(null, CouldNotUnderstand);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not transcribe a Telegram voice note: {Message}", ex.Message);
            return new Dictation(null, CouldNotUnderstand);
        }
    }

    private async Task<ReadOnlyMemory<byte>> DownloadAsync(
        ITelegramBotClient botClient, Voice voice, CancellationToken ct)
    {
        var file = await botClient.GetFile(voice.FileId, ct);
        if (file.FilePath is null)
        {
            throw new InvalidDataException($"Telegram gave no path for voice note {voice.FileId}");
        }

        using var buffer = new MemoryStream();
        await botClient.DownloadFile(file.FilePath, buffer, ct);
        logger.LogDebug(
            "Transcribing a {Seconds}s Telegram voice note of {Bytes} bytes", voice.Duration, buffer.Length);
        return buffer.ToArray();
    }

    private static TranscriptionRequest Prepare(ReadOnlyMemory<byte> audio, AudioContainer container) =>
        container.NeedsDecoding
            ? new TranscriptionRequest
            {
                Audio = WavAudio.FromPcm(
                    OpusVoiceNote.DecodeToPcm(audio),
                    OpusVoiceNote.SampleRateHz, OpusVoiceNote.Channels, OpusVoiceNote.SampleWidthBytes),
                MediaType = WavAudio.MediaType
            }
            : new TranscriptionRequest { Audio = audio, MediaType = container.MediaType };

    // The same gate the satellites use, for the same reason: whisper answers a recording of nothing
    // with a plausible sentence it has seen in a thousand subtitle files. A null signal is not a bad
    // one — Lemonade emits none on a plain json body — so it fails open. WebChat is deliberately not
    // gated: the person there reads the words before sending, and the floors would only delete text
    // they can see is right.
    private bool IsWorthATurn(TranscriptionResult result) =>
        !string.IsNullOrWhiteSpace(result.Text)
        && (result.AvgLogProb ?? double.MaxValue) >= settings.AvgLogProbThreshold
        && (result.NoSpeechProb ?? double.MinValue) <= settings.NoSpeechProbThreshold;

    private string TooLongReply() =>
        $"That voice note is too long: I can only transcribe up to {Describe(settings.MaxLength)}.";

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} minutes" : $"{span.TotalSeconds:0} seconds";
}