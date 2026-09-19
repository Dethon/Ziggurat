namespace McpChannelVoice.Settings;

public record ApprovalSettings
{
    public ApprovalJudgmentSettings Judgment { get; init; } = new();
}

// The bars the reader acts by, in this server's own appsettings alone: no other host has to agree,
// and each is a config edit with a probe run behind it (.scratch/jev-voice-approval/probe). They
// were set on 32 spoken answers measured 2026-09-18: no wrong action at the sure bars, and the
// lean bar rescued "sí sí" — which the word list already approved — from a re-ask.
public record ApprovalJudgmentSettings
{
    // Off is the word list alone, exactly as before Jev; an empty TypeSafe key is the same thing
    // without a flag.
    public bool Enabled { get; init; } = true;

    // From the moment the judge is asked. The person is standing at the satellite, so a late
    // answer is worth less than the word list's immediate one: a warm judgment measured about
    // 330 ms, and this sits above that tail.
    public int DeadlineMs { get; init; } = 1000;

    // Jev alone acts: one probability at or above this while the other sits at or below Counter.
    public double Sure { get; init; } = 0.9;

    public double Counter { get; init; } = 0.1;

    // Jev leans: it acts only beside a word list that leans the same way.
    public double Lean { get; init; } = 0.5;
}