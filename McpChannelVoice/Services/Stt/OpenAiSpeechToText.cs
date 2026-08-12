using Domain.Contracts;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Transcription;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Stt;

// The satellites' end of the shared transcription client: one utterance segment arrives as a chunk
// stream, is buffered into a WAV blob (mono s16le at the incoming rate — the satellites send
// 16 kHz), and is posted through the injected IAudioTranscriber like every other dictation. What
// stays here is what only the hub has: the chunk stream itself and the whisper biasing prompt,
// which is composed per satellite from the room, the locality and the prior segment's text.
public sealed class OpenAiSpeechToText(
    IAudioTranscriber transcriber,
    OpenAiSttConfig config) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in audio.WithCancellation(ct))
        {
            chunks.Add(chunk);
        }

        var dataBytes = chunks.Sum(c => c.Data.Length);
        if (dataBytes == 0)
        {
            return new TranscriptionResult { Text = "" };
        }

        return await transcriber.TranscribeAsync(
            new TranscriptionRequest
            {
                Audio = WavAudio.FromPcm(Concatenate(chunks, dataBytes), chunks[0].Format),
                MediaType = WavAudio.MediaType,
                FileName = "utterance.wav",
                Language = options.Language,
                Prompt = WhisperPromptBuilder.Build(
                    options.PromptTemplate ?? config.Prompt, options.Room, options.Locality,
                    options.PriorText, config.MaxPromptChars)
            },
            ct);
    }

    private static byte[] Concatenate(IReadOnlyList<AudioChunk> chunks, int dataBytes)
    {
        var pcm = new byte[dataBytes];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.Data.Span.CopyTo(pcm.AsSpan(offset));
            offset += chunk.Data.Length;
        }
        return pcm;
    }
}