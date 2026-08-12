namespace WebChat.Client.State.Composer;

public enum DictationStatus
{
    Idle,

    // The microphone is open and the person is holding the button down.
    Recording,

    // The same dictation, the same clock, the same audio still accumulating — only the way it can
    // end has changed, from letting go to pressing.
    Latched,

    // The recording is over and the words have not come back yet.
    Transcribing
}

// Which control the composer offers on the right. The streaming cancel takes precedence over
// everything, exactly as it did before there was a microphone.
public enum SendControl
{
    Send,
    Microphone,
    Cancel
}

// Words waiting to be put into the composer. The stamp is what makes the same sentence dictated
// twice two events rather than one: a person who repeats themselves must still get both.
public sealed record PendingTranscript(string Text, long Stamp);

public sealed record DictationState
{
    public DictationStatus Status { get; init; } = DictationStatus.Idle;

    // A refused permission, or a browser without the APIs: the control stops trying for the
    // session rather than staying dead under a finger that keeps pressing it.
    public bool Unavailable { get; init; }

    // The composer's existing one-line refusal slot, shared with the attachment capability
    // refusal. Cleared as soon as the next dictation starts.
    public string? Refusal { get; init; }

    // A press too short to be a hold. Not a refusal — nothing went wrong, the person just tapped.
    public string? Hint { get; init; }

    public PendingTranscript? Transcript { get; init; }

    public bool IsRecording => Status is DictationStatus.Recording or DictationStatus.Latched;
}