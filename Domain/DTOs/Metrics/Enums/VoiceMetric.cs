namespace Domain.DTOs.Metrics.Enums;

// Persisted as integers in metric events (Redis), so values are pinned explicitly: never renumber or
// reuse a value. Reordering/removing a member silently re-labels historical data — removing
// AudioSeconds once shifted every later value and corrupted stored voice metrics. Append new
// members with the next free number. Guarded by VoiceEnumsTests.
public enum VoiceMetric
{
    WakeTriggered = 0,
    UtteranceTranscribed = 1,
    SttLatencyMs = 2,
    TtsLatencyMs = 3,
    WakeToFirstAudioMs = 4,
    ApprovalResolved = 5,
    SttError = 6,
    TtsError = 7,
    AnnouncePlayed = 8,
    AnnounceQueued = 9,
    AnnounceError = 10,
    AnnouncePreemptedReply = 11,
    FollowUpWindowOpened = 12,
    FollowUpEngaged = 13,
    FollowUpTimedOut = 14,
    AlarmAcknowledged = 15,
    AlarmUnacknowledged = 16,
    AlarmOffline = 17,
    UtteranceRejected = 18,
    TseInvoked = 19,
    TseSkipped = 20,
    TseFailed = 21,
    TseLatencyMs = 22,
    // Turn decomposition: EndpointTailMs..SpeechEndToFirstAudioMs split the wake→first-audio span
    // into the parts nothing measured before. SpeechEndToFirstAudioMs is the user-perceived one —
    // WakeToFirstAudioMs starts at mic-open, so it also contains the user's own speech. The four
    // others nest inside it and, with SttLatencyMs and TtsLatencyMs, tile it exactly:
    // EndpointTail + SpeakerVerify + Stt + AgentRoundTrip + ReplyQueueWait + Tts = SpeechEndToFirstAudio
    // (guarded by VoiceTurnDecompositionTests).
    EndpointTailMs = 23,
    SpeakerVerifyMs = 24,
    AgentRoundTripMs = 25,
    ReplyQueueWaitMs = 26,
    SpeechEndToFirstAudioMs = 27,
    // SpeakerVerifyMs is the FINAL, inline verification pass (capture close -> STT), which is part of
    // the tiling above. The early mid-capture pass runs while the capture is still open, concurrent
    // with the user speaking, so it overlaps the utterance and is NOT part of it — separate member so
    // a grouping that isn't keyed on Outcome cannot blend an overlapping span into an additive one.
    SpeakerVerifyEarlyMs = 28,
    // Multi-satellite wake arbitration: a co-heard wake that lost (Outcome carries why), and a
    // mid-conversation handoff where the conversation binding moved to the winning satellite.
    WakeSuppressed = 29,
    WakeHandoff = 30,
    // A decode started during a speech pause, before the gate ended the capture (Stt.SpeculateAfterMs).
    // DurationMs is the decode's own time; Outcome says whether the turn used it or speech resumed
    // and it was thrown away. Not part of the tiling above: a used one overlaps the endpoint tail,
    // and SttLatencyMs is then only what was still left to wait for once the capture closed.
    SpeculativeSttMs = 31
}