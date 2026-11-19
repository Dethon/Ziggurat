using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Tse;

// STT-path target-speaker extraction (spec: 2026-07-22-tse-live-integration-design.md).
// Sits between the host and the segmented/wyoming STT chain. Fail-open on every path: the
// inner backend always gets audio — extracted when the sidecar delivered, raw otherwise.
// The gate and endpointing never see this class; only the STT input changes.
public sealed class TseSpeechToText(
    ISpeechToText inner,
    TseSettings settings,
    ITseExtractorClient client,
    TseAuditTrail audit,
    IMetricsPublisher metrics,
    ILogger<TseSpeechToText> logger) : ISpeechToText
{
    public static ISpeechToText Wrap(
        ISpeechToText inner, TseSettings settings, ITseExtractorClient client, TseAuditTrail audit,
        IMetricsPublisher metrics, ILoggerFactory loggers) =>
        settings.Mode == TseMode.Off
            ? inner
            : new TseSpeechToText(inner, settings, client, audit, metrics, loggers.CreateLogger<TseSpeechToText>());

    // What this stage wraps. A decode started before the speaker check (SpeculativeTranscription)
    // runs on it, since nobody yet knows whether there is a target to extract.
    public ISpeechToText Inner => inner;

    // Whether this stage would pass the turn straight to Inner. Only then can a decode of the raw
    // audio stand in for this stage's, and the turn reports the skip as this stage would have.
    public bool Skips(TranscriptionOptions options) => SkipReason(options) is not null;

    public void ReportSkipped(TranscriptionOptions options) =>
        Publish(VoiceMetric.TseSkipped, options, outcome: SkipReason(options));

    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
    {
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in audio.WithCancellation(ct))
        {
            chunks.Add(chunk);
        }

        var skip = SkipReason(options);
        if (skip is not null)
        {
            Publish(VoiceMetric.TseSkipped, options, outcome: skip);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        var mixture = WavCodec.Encode(chunks);
        var stopwatch = Stopwatch.StartNew();
        var reply = await client.ExtractAsync(mixture, options.TargetSpeaker!, ct);
        stopwatch.Stop();

        if (reply.Wav is null)
        {
            var outcome = reply.Rejected ? "rejected" : "unavailable";
            logger.LogWarning("TSE sidecar {Outcome} for {Speaker}; raw audio proceeds", outcome, options.TargetSpeaker);
            Publish(VoiceMetric.TseFailed, options, outcome, durationMs: stopwatch.ElapsedMilliseconds);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        AudioChunk extracted;
        try
        {
            extracted = WavCodec.Decode(reply.Wav);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "TSE reply malformed for {Speaker}; raw audio proceeds", options.TargetSpeaker);
            Publish(VoiceMetric.TseFailed, options, outcome: "malformed", durationMs: stopwatch.ElapsedMilliseconds);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        if (extracted.Data.Length == 0)
        {
            logger.LogWarning("TSE reply empty for {Speaker}; raw audio proceeds", options.TargetSpeaker);
            Publish(VoiceMetric.TseFailed, options, outcome: "empty", durationMs: stopwatch.ElapsedMilliseconds);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        Publish(VoiceMetric.TseInvoked, options, outcome: "ok");
        Publish(VoiceMetric.TseLatencyMs, options, durationMs: stopwatch.ElapsedMilliseconds);
        audit.Record(
            options.TargetSpeaker!, options.SatelliteId, options.NoiseFloorRms,
            stopwatch.ElapsedMilliseconds, mixture, reply.Wav);
        return await inner.TranscribeAsync(Replay(Rechunk(extracted, chunks)), options, ct);
    }

    // WavCodec.Decode hands back the whole reply as one contiguous chunk. Feeding that
    // straight to the inner STT starves SegmentedSpeechToText's gate: with a single frame,
    // AdaptiveLevelTracker's smoothing/min-window each hold exactly one entry, so the frame
    // becomes its own noise floor and IsSpeech can never clear floor + EnterMarginDb. Re-slice
    // the extracted audio to the original capture's frame cadence so the gate sees the same
    // rhythm it was calibrated on. The extracted payload may legitimately be shorter than the
    // mixture (the sidecar clamps its output) — stop once it is exhausted rather than assuming
    // matching lengths.
    private static IReadOnlyList<AudioChunk> Rechunk(AudioChunk extracted, IReadOnlyList<AudioChunk> original)
    {
        var frames = new List<AudioChunk>();
        var offset = 0;
        foreach (var chunk in original)
        {
            if (offset >= extracted.Data.Length)
            {
                break;
            }
            var length = Math.Min(chunk.Data.Length, extracted.Data.Length - offset);
            frames.Add(extracted with { Data = extracted.Data.Slice(offset, length) });
            offset += length;
        }
        if (offset < extracted.Data.Length)
        {
            frames.Add(extracted with { Data = extracted.Data.Slice(offset) });
        }
        return frames;
    }

    private string? SkipReason(TranscriptionOptions options) =>
        options.TargetSpeaker is null ? "no_speaker"
        : settings.Mode == TseMode.Auto && (options.NoiseFloorRms ?? 0) < settings.NoiseFloorThreshold ? "quiet"
        : null;

    // Stamped like every other voice report: Identity is the satellite's configured identity, and
    // the person being extracted is the Speaker. This publisher used to write the target speaker
    // into Identity, which is the one place the two meanings crossed.
    private void Publish(
        VoiceMetric metric, TranscriptionOptions options, string? outcome = null, long? durationMs = null) =>
        metrics.Publish(new VoiceEvent
        {
            Metric = metric,
            Speaker = options.TargetSpeaker,
            Outcome = outcome,
            DurationMs = durationMs,
            FloorRms = options.NoiseFloorRms
        }.About(SatelliteIdentity.Of(options)));

    private static async IAsyncEnumerable<AudioChunk> Replay(IReadOnlyList<AudioChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
        await Task.CompletedTask;
    }
}