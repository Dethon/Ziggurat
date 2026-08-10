using Infrastructure.Clients.Transcription;

namespace McpChannelSignalR.Settings;

// What only the browser's dictation needs on top of the shared chat settings: the mis-tap floor
// the composer obeys and the byte cap the endpoint enforces.
public record DictationSettings : ChatDictationSettings
{
    // Below this a press is a mis-tap: nothing is recorded and nothing is sent.
    public TimeSpan MinLength { get; init; } = TimeSpan.FromMilliseconds(400);

    // The cap in bytes, at the one format the browser sends — 16 kHz mono s16le WAV — plus its
    // header and a tenth for headroom. A duration cannot be enforced on a blob without decoding it,
    // and the point of the check is to refuse the oversized post before anything decodes anything.
    // The headroom is not slack: the browser stops itself on a frame boundary rather than a sample
    // one, so a dictation that ran exactly to the cap is a little over the arithmetic, and a
    // ceiling without it would refuse the longest legal recording there is.
    public long MaxBytes => 44 + (long)(MaxLength.TotalSeconds * 16_000 * 2 * 1.1);
}