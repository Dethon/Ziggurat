using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services;

// The microphone for one connection's turn-taking: opening it, closing it and reading its
// statistics at exactly that moment, and the two events that tell the satellite what its indicator
// should show.
//
// Closing a capture and recording what it taught us about the room are one operation here, so a new
// turn-taking path cannot do the first and forget the second. The approval mic is a separate,
// one-shot capture that does not come through here; it takes its gate from the same factory.
public sealed class CaptureSession(
    SatelliteSession session,
    SilenceGateFactory gates,
    TimeProvider time,
    TimeSpan historySpan,
    // Called as a wake turn opens, with what the satellite reported about the wake. There is no
    // follow-up counterpart on purpose: a follow-up has no wake of its own, and this hook is where
    // the wake metric is published.
    Action<WakeAnnouncement?> onWakeTurn,
    // Hears every turn capture's pauses, wake and follow-up alike, so a decode can start before the
    // gate ends the capture. The approval microphone does not come through here and has none.
    ICapturePauseListener? pauses = null)
{
    // The playback anchors, which the queue carries but does not expose: holding ITurnAnchor is
    // what says this type's captures are turns, and the microphone the approval prompt holds has
    // no way to reach them.
    private readonly ITurnAnchor _turn = session.Playback;

    public UtteranceCapture OpenWakeTurn(WakeAnnouncement? announcement)
    {
        // The turn opens here, so the playback loop can report turn -> first-audio latency.
        _turn.MarkTurnStart(time.GetTimestamp());
        onWakeTurn(announcement);
        // Paid in immediately before the gate below reads it back, so "record before the gate is
        // built" is a fact about this method rather than an order two files have to agree on. A
        // legacy satellite sends nothing and records a zero, which the room-noise memory drops as an
        // absent measurement rather than treating as a silent room.
        //
        // It follows that a wake frame arriving while a turn is already open pays nothing in — the
        // coordinator returns before reaching here. That is the trade for the memory having exactly
        // one payment site: a reading is kept only when it describes the capture it is about to cap,
        // and the satellite that sends a second announcement for a turn it already opened is the
        // only one that loses a sample.
        gates.RecordRoomLevel(session.SatelliteId, announcement?.RoomRms ?? 0);
        return Open();
    }

    // Takes nothing, and never will: a follow-up window reopens the microphone wake-free, so there
    // is no announcement to carry and no wake to report.
    public UtteranceCapture OpenFollowUpTurn()
    {
        _turn.MarkTurnStart(time.GetTimestamp());
        return Open();
    }

    private UtteranceCapture Open() =>
        session.Mic.Open(
            gates.Create(session.SatelliteId, session.Config),
            // Rule B asks an already-open capture, retrospectively, what it heard during another
            // satellite's wake-word span — so every capture has to remember.
            new ChunkHistory(time, historySpan),
            pauses);

    // Returns the gate statistics frozen at this instant. The endpointing tail is what anchors
    // speech end and it must not be re-read later: Feed keeps accepting frames until the satellite
    // gets its closing transcript, so a later read would report the tail plus an arbitrary delay.
    public CaptureStats Close(UtteranceCapture capture)
    {
        // Closing and paying back into the room-noise memory are one act on the microphone, so a
        // turn cannot close without paying.
        var stats = session.Mic.Close(capture);
        // Only an utterance that ended goes on to be transcribed and claim its pause's decode.
        if (capture.Pause is { } pause
            && capture.Completed is not { IsCompletedSuccessfully: true, Result: CaptureOutcome.Ended })
        {
            pauses?.Dropped(pause);
        }
        // Stamped with the host's TimeProvider — the same instance the playback loop reads,
        // which reads it back. The frozen endpointing tail rewinds the close to the instant the user
        // actually stopped talking; read here, at the close, because it is the last point where the
        // tail is known to be the one the gate ended on.
        _turn.MarkSpeechEnd(time.GetTimestamp(), stats.TrailingSilenceMs, time);
        return stats;
    }

    // Tells the satellite the user has stopped speaking and processing has begun — this drives its
    // Thinking indicator.
    public Task SpeechStoppedAsync(CancellationToken ct) =>
        SendIndicatorAsync("voice-stopped", ct);

    // Tells the satellite the mic is live again for a wake-free follow-up turn — this returns its
    // indicator from Thinking to Listening. It cannot infer the moment: its own capture never
    // closed, so from its side a reply draining looks the same whether the agent is mid-answer or
    // done.
    public Task ListeningStartedAsync(CancellationToken ct) =>
        SendIndicatorAsync("listening-started", ct);

    // Both events are indicator-only by contract: a failed write must never cost the user the
    // utterance or the window it was announcing, so they go through the session's best-effort
    // writer, which reports failure as false rather than throwing. An already-cancelled connection
    // still stops the loop here; a cancellation raised mid-write is swallowed with everything else,
    // and the loop's next await on the same token throws instead.
    private Task SendIndicatorAsync(string type, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return session.TrySendControlAsync(WyomingEvent.Header(type, new JsonObject()), ct);
    }
}