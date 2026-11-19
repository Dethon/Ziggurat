using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services.Stt;

// One satellite's decode-ahead: when a turn capture's speech pauses past the mark, the audio so far
// is decoded while the gate is still waiting out its trailing silence. If the capture then ends on
// that same pause the turn takes the decode (Claim); speech resuming, a newer pause or a claim for
// another pause throws it away. At most one decode is in flight — the newest pause's — because the
// server decodes one request at a time and a stale one only delays the next.
//
// Nothing a decode does is visible until a turn takes it: it publishes no transcript and no TSE or
// verification event, and reports itself only as "used" or "discarded" (SpeculativeSttMs).
public sealed class SpeculativeTranscription(
    ISpeechToText speechToText,
    TimeSpan pauseAfter,
    Func<CaptureStats, TranscriptionOptions> optionsFor,
    Action<string, long> report) : ICapturePauseListener
{
    private readonly Lock _lock = new();
    private Attempt? _current;

    public TimeSpan PauseAfter => pauseAfter;

    public void Paused(CapturePause pause)
    {
        var started = Attempt.Start(pause, speechToText, optionsFor(pause.Stats), report);
        Attempt? replaced;
        lock (_lock)
        {
            replaced = _current;
            _current = started;
        }
        replaced?.Discard();
    }

    public void Resumed(CapturePause pause) => DiscardFor(pause);

    public void Dropped(CapturePause pause) => DiscardFor(pause);

    private void DiscardFor(CapturePause pause)
    {
        Attempt? resumed = null;
        lock (_lock)
        {
            if (_current?.Pause == pause)
            {
                resumed = _current;
                _current = null;
            }
        }
        resumed?.Discard();
    }

    // The decode for the pause a capture ended on, handed over exactly once. Whatever else was in
    // flight is not this turn's and is thrown away.
    public Attempt? Claim(CapturePause? endedOn)
    {
        Attempt? current;
        lock (_lock)
        {
            current = _current;
            _current = null;
        }

        if (current is not null && endedOn is not null && current.Pause == endedOn)
        {
            return current;
        }
        current?.Discard();
        return null;
    }

    public sealed class Attempt
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Action<string, long> _report;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private int _settled;
        private long _decodeMs;

        private Attempt(CapturePause pause, Action<string, long> report)
        {
            Pause = pause;
            _report = report;
        }

        public CapturePause Pause { get; }

        private Task<TranscriptionResult> Decode { get; set; } = null!;

        internal static Attempt Start(
            CapturePause pause, ISpeechToText speechToText, TranscriptionOptions options, Action<string, long> report)
        {
            var attempt = new Attempt(pause, report);
            attempt.Decode = Task.Run(async () =>
            {
                try
                {
                    return await speechToText.TranscribeAsync(Replay(pause.Audio), options, attempt._cts.Token);
                }
                finally
                {
                    Volatile.Write(ref attempt._decodeMs, attempt.ElapsedMs);
                }
            });
            // Observed here so a discarded decode's fault never surfaces as an unobserved exception.
            _ = attempt.Decode.ContinueWith(
                static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return attempt;
        }

        // The decode's transcript, or null when it failed — the caller then decodes the capture the
        // ordinary way. A failure is reported as one, never as used.
        public async Task<TranscriptionResult?> UseAsync(CancellationToken ct)
        {
            try
            {
                var result = await Decode.WaitAsync(ct);
                // The decode's own length, not how long the turn waited for it: that is SttLatencyMs.
                Settle("used", Volatile.Read(ref _decodeMs));
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Settle("failed", ElapsedMs);
                return null;
            }
        }

        // A no-op once the attempt was used, so a turn can discard unconditionally on its way out.
        public void Discard()
        {
            // A decode that already finished cost its own length, however long it then sat unclaimed.
            if (Settle("discarded", Decode.IsCompleted ? Volatile.Read(ref _decodeMs) : ElapsedMs))
            {
                _cts.Cancel();
            }
        }

        private long ElapsedMs => (long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;

        private bool Settle(string outcome, long durationMs)
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0)
            {
                return false;
            }
            _report(outcome, durationMs);
            return true;
        }

        private static async IAsyncEnumerable<AudioChunk> Replay(IReadOnlyList<AudioChunk> chunks)
        {
            foreach (var chunk in chunks)
            {
                yield return chunk;
            }
            await Task.CompletedTask;
        }
    }
}