namespace McpChannelVoice.Settings;

public record SttSettings
{
    public OpenAiSttConfig OpenAi { get; init; } = new();
    public SegmentedSttConfig Streaming { get; init; } = new();
}

public record OpenAiSttConfig
{
    public string BaseUrl { get; init; } = "http://lemonade:13305/v1";

    // Lemonade catalog name. The cpu and gpu tiers run the same whisper.cpp engine on the same
    // model (only the device flips), so STT_BACKEND never changes this — it is a container-side
    // concern. Override only to trade accuracy for speed (Whisper-Medium / Whisper-Small) or the
    // reverse (Whisper-Large-v3), or to name a `user.` model the lemonade entrypoint registered.
    public string Model { get; init; } = "Whisper-Large-v3-Turbo";
    public string? Language { get; init; }

    // Initial prompt posted with every transcription: it biases spelling and vocabulary, and on a
    // one-to-three-second command it carries proportionally far more weight than on a paragraph.
    // Supports {room} and {locality}, filled from the capturing satellite. A per-request prompt
    // replaces whisper-server's own --prompt for that request, so this is authoritative for hub
    // traffic and the container default only serves other callers.
    public string? Prompt { get; init; }

    // Character approximation of whisper's 224-token prompt window, deliberately under it.
    public int MaxPromptChars { get; init; } = 700;

    // Gibberish gate: drop transcripts whose avg_logprob falls below the floor or whose
    // no_speech_prob rises above the ceiling. Null signals fail open (TranscriptDispatcher).
    public double AvgLogProbThreshold { get; init; } = -1.0;
    public double NoSpeechProbThreshold { get; init; } = 0.6;

    // avg_logprob falls with utterance length for reasons that have nothing to do with being
    // wrong — measured on prod, a 2.9 s clip scored -0.12 and a 0.75 s clip -0.23 — so a single
    // floor drops correct short commands more readily than correct long ones. Below
    // FullThresholdSpeechMs of measured speech the looser floor applies. Mirrors the pair
    // SpeakerVerification already uses for the same reason.
    public double ShortSpeechAvgLogProbThreshold { get; init; } = -1.4;
    public int FullThresholdSpeechMs { get; init; } = 2000;

    // Bounds the transcription POST only — audio capture length is the speaker's business. The
    // shared Lemonade HttpClient has an infinite timeout for streaming TTS, so without this a
    // Lemonade that accepts connections but never answers stalls the utterance indefinitely.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public record SegmentedSttConfig
{
    public bool Enabled { get; init; }
    public double SilenceRmsThreshold { get; init; } = 500;
    public int SegmentSilenceMs { get; init; } = 350;
    public int MinSegmentMs { get; init; } = 800;
    public int MaxInFlightDecodes { get; init; } = 1;

    // Audio that must accumulate before the segmenting gate is allowed to split at all. A short
    // command decoded whole beats the same command decoded as two context-free fragments —
    // measured on prod, splitting "Pon el temporizador de 10 minutos en la cocina" produced a
    // wrong verb and a duplicated number. Only the FIRST split is gated: once an utterance has
    // proven itself long, later splits keep the overlap-with-speech latency win.
    public int FirstSplitAfterMs { get; init; } = 4000;

    // Feed each segment the previous segment's transcript as whisper's initial prompt, so a
    // fragment is decoded as the continuation it is rather than as a standalone utterance. This
    // serializes decodes by construction, which is why MaxInFlightDecodes buys nothing while it
    // is on (SegmentedSpeechToText.Wrap warns if both are set).
    public bool ChainContext { get; init; } = true;
}