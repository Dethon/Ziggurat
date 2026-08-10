using Domain.Contracts;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Transcription;
using McpChannelTelegram.Settings;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace McpChannelTelegram.Services;

// One voice note turned into words: fetched from Telegram, decoded if whisper cannot read the
// container itself, and transcribed. Nothing is stored — no attachment reference is minted, and
// the bytes live only as long as the request.
public sealed class VoiceNoteDictation(
    IAudioTranscriber transcriber,
    DictationSettings settings,
    ILogger<VoiceNoteDictation> logger)
{
    public async Task<string> ReadAsync(ITelegramBotClient botClient, Voice voice, CancellationToken ct)
    {
        var file = await botClient.GetFile(voice.FileId, ct);
        if (file.FilePath is null)
        {
            throw new InvalidDataException($"Telegram gave no path for voice note {voice.FileId}");
        }

        using var buffer = new MemoryStream();
        await botClient.DownloadFile(file.FilePath, buffer, ct);

        var audio = buffer.ToArray();
        logger.LogDebug("Transcribing a {Seconds}s Telegram voice note of {Bytes} bytes", voice.Duration, audio.Length);

        var result = await transcriber.TranscribeAsync(
            new TranscriptionRequest
            {
                Audio = WavAudio.FromPcm(
                    OpusVoiceNote.DecodeToPcm(audio),
                    OpusVoiceNote.SampleRateHz, OpusVoiceNote.Channels, OpusVoiceNote.SampleWidthBytes),
                MediaType = WavAudio.MediaType,
                Language = settings.Transcription.Language
            },
            ct);

        return result.Text.Trim();
    }
}