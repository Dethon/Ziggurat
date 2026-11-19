using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services;

// One satellite's microphone: being open, being closed, and what the open capture is hearing. It
// knows nothing about turns — a wake turn, a follow-up turn and a question the agent asks mid-turn
// each open one, and only the first two are turns. CaptureSession sits on top of this and adds the
// turn semantics; a caller that is not opening a turn holds this instead, and cannot anchor one.
//
// Closing a capture and telling the room-noise memory what it measured are one act here, so a new
// listening path cannot do the first and forget the second. A capture that only reads would let the
// memory expire on a satellite used mostly for approvals.
public sealed class Microphone(string satelliteId, SilenceGateFactory gates)
{
    private UtteranceCapture? _capture;

    public UtteranceCapture Open(
        SilenceGate gate, ChunkHistory? history = null, ICapturePauseListener? pauses = null)
    {
        var capture = new UtteranceCapture(gate, history, pauses);
        Volatile.Write(ref _capture, capture);
        return capture;
    }

    // Returns the gate statistics frozen at this instant. Feed keeps accepting frames until the
    // satellite is told the turn is over, so a later read would report a different tail.
    //
    // Detaching is by identity: only the capture being closed is unhooked, so a late close from one
    // that has already been replaced leaves the live capture receiving audio. The stats are the
    // closed capture's either way — they are read off the argument, not off whatever is attached.
    public CaptureStats Close(UtteranceCapture capture)
    {
        Interlocked.CompareExchange(ref _capture, null, capture);
        var stats = capture.Stats;
        gates.RecordCaptureClose(satelliteId, stats);
        return stats;
    }

    // An observation point with no production caller: it says whether this satellite is listening,
    // and it sits on the thing being observed.
    public bool IsOpen => Volatile.Read(ref _capture) is not null;

    public void Feed(AudioChunk chunk) => Volatile.Read(ref _capture)?.Feed(chunk);

    public void ForceEnd() => Volatile.Read(ref _capture)?.ForceEnd();

    // What the open capture has been hearing. Arbitration's Rule B asks this of a satellite that
    // did not claim the wake, to find out whether it heard the same utterance.
    public CaptureActivity? Activity =>
        Volatile.Read(ref _capture)?.History is { } history
            ? new CaptureActivity(history.OpenedAt, history.Snapshot())
            : null;

    public bool TryAbort() => Volatile.Read(ref _capture)?.Abort() ?? false;
}