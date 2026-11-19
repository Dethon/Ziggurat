using System.Threading.Channels;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services;

public enum CaptureOutcome
{
    Ended,
    NoSpeech,
    Abandoned
}

// Audio-level facts about one capture, published on UtteranceTranscribed metrics so the
// RMS/min-speech entry bar and the adaptive-floor margins can be tuned from real data
// instead of guesswork.
// MeasuredFloorRms is the gate's own reading of the background before the remembered room level
// caps it (FloorRms is what the gate actually used). It is what RoomNoiseMemory learns from, so a
// remembered level is never re-derived from itself.
public readonly record struct CaptureStats(
    double PeakRms, double FloorRms, long SpeechMs, string? EndReason, double TrailingRms = 0,
    long TrailingSilenceMs = 0, double MeasuredFloorRms = 0);

// One bounded mic capture over the held-open Wyoming stream. The read loop pushes audio
// via Feed (single-threaded); the gate decides when speech ends (Ended) or the no-speech
// window expires (NoSpeech). Completed settles exactly once; Audio replays the buffered chunks.
// A pause in an open capture's speech, long enough that the gate may yet end the utterance on it:
// the audio up to that moment and the gate's statistics as they stood. Compared by identity — a
// capture ends "on" a pause only if no speech arrived after this very one was taken.
public sealed class CapturePause(IReadOnlyList<AudioChunk> audio, CaptureStats stats)
{
    public IReadOnlyList<AudioChunk> Audio { get; } = audio;
    public CaptureStats Stats { get; } = stats;
}

// Told, on the Wyoming read loop, when an open capture's speech has paused for PauseAfter and when
// it resumes, and, as the capture closes, when it closed in a pause without ending the utterance
// (no speech, arbitration, an early reject) so nothing will come to claim it. None may wait.
public interface ICapturePauseListener
{
    TimeSpan PauseAfter { get; }
    void Paused(CapturePause pause);
    void Resumed(CapturePause pause);
    void Dropped(CapturePause pause);
}

public sealed class UtteranceCapture(
    SilenceGate gate, ChunkHistory? history = null, ICapturePauseListener? pauses = null)
{
    private CapturePause? _pause;

    private readonly Channel<AudioChunk> _chunks = Channel.CreateUnbounded<AudioChunk>();
    private readonly TaskCompletionSource<CaptureOutcome> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _forced;
    private readonly List<AudioChunk> _audio = [];

    public Task<CaptureOutcome> Completed => _done.Task;

    public IAsyncEnumerable<AudioChunk> Audio => _chunks.Reader.ReadAllAsync();

    public ChunkHistory? History => history;

    // The pause the capture is in, if it is in one. Once the capture has ended it no longer moves,
    // so after an end it names the pause the utterance ended on (null when speech never paused for
    // the listener's mark, or there is no listener).
    public CapturePause? Pause => Volatile.Read(ref _pause);

    // The full continuous capture — every fed chunk, buffered so the speaker verifier embeds
    // enrollment-matching continuous audio (silence-cut speech-only fragments collapse CAM++
    // similarity). Returned as a snapshot under lock: the early-close check reads it mid-capture
    // on the conversation task while Feed appends on the Wyoming read loop.
    public IReadOnlyList<AudioChunk> BufferedAudio
    {
        get { lock (_audio) { return _audio.ToArray(); } }
    }

    public CaptureStats Stats => new(
        gate.PeakRms,
        gate.FloorRms,
        (long)gate.SpeechElapsed.TotalMilliseconds,
        _forced ? "forced" : gate.EndReason,
        gate.TrailingRms,
        (long)gate.TrailingSilence.TotalMilliseconds,
        gate.MeasuredFloorRms);

    public void Feed(AudioChunk chunk)
    {
        var decision = gate.Process(
            chunk.Data.Span, chunk.Format.SampleRateHz, chunk.Format.SampleWidthBytes, chunk.Format.Channels);
        history?.Record(gate.LastChunkRms, gate.LastChunkWasSpeech);
        lock (_audio)
        {
            _audio.Add(chunk);
        }
        _chunks.Writer.TryWrite(chunk);
        TrackPause();

        switch (decision)
        {
            case SilenceGate.Decision.EndUtterance:
                _chunks.Writer.TryComplete();
                _done.TrySetResult(CaptureOutcome.Ended);
                break;
            case SilenceGate.Decision.NoSpeech:
                _chunks.Writer.TryComplete();
                _done.TrySetResult(CaptureOutcome.NoSpeech);
                break;
        }
    }

    // Ahead of the terminal decision on purpose: the chunk that ends the capture must not move the
    // pause, and nothing after the end may either (frames keep arriving until the satellite is told).
    private void TrackPause()
    {
        if (pauses is null || _done.Task.IsCompleted)
        {
            return;
        }

        if (gate.LastChunkWasSpeech)
        {
            if (Interlocked.Exchange(ref _pause, null) is { } resumed)
            {
                pauses.Resumed(resumed);
            }
            return;
        }

        if (_pause is null && gate.HasEndableSpeech && gate.TrailingSilence >= pauses.PauseAfter)
        {
            var pause = new CapturePause(BufferedAudio, Stats);
            Volatile.Write(ref _pause, pause);
            pauses.Paused(pause);
        }
    }

    // Arbitration loss/steal: settle as Abandoned so the conversation loop exits without
    // dispatching and without its own wire write (the arbiter owns the pause). Returns false
    // when the capture already ended naturally — the caller must then leave the turn alone.
    public bool Abort()
    {
        if (!_done.TrySetResult(CaptureOutcome.Abandoned))
        {
            return false;
        }
        _chunks.Writer.TryComplete();
        return true;
    }

    public void ForceEnd()
    {
        // Feed/ForceEnd are serialized on the same Wyoming read loop: a plain completed-check
        // is enough to stop a late audio-stop from overwriting a natural end's EndReason.
        if (!_done.Task.IsCompleted)
        {
            _forced = true;
        }
        _chunks.Writer.TryComplete();
        _done.TrySetResult(CaptureOutcome.Ended);
    }
}