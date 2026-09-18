using JetBrains.Annotations;

namespace Domain.Memory;

// The three memory judgments' bars, in the agent's own appsettings alone: no other host has to
// agree, and each is a config edit with a probe run behind it. Every bar was set from the probe
// of 2026-09-18 (`.scratch/jev-memory-judgments/probe/`): the gate's 0.1 skipped 10 of 12 empty
// windows and none of 14 that held a memory; the check's 0.5 dropped all 13 junk candidates and
// kept every keeper once an instruction was judged on `supported` alone.
public sealed record MemoryJudgmentSettings
{
    public bool Enabled { get; [UsedImplicitly] init; } = true;

    public GateSettings Gate { get; [UsedImplicitly] init; } = new();

    public VerifySettings Verify { get; [UsedImplicitly] init; } = new();

    public PairSettings Pairs { get; [UsedImplicitly] init; } = new();

    // The extractor is skipped only when every one of the three questions is at or below this;
    // anything else, including no answer, extracts as today.
    public sealed record GateSettings
    {
        public double SkipAtOrBelow { get; [UsedImplicitly] init; } = 0.1;
    }

    // A candidate is stored only when each question clears its own bar; an Instruction is judged
    // on `supported` alone.
    public sealed record VerifySettings
    {
        public double Supported { get; [UsedImplicitly] init; } = 0.5;
        public double AboutUser { get; [UsedImplicitly] init; } = 0.5;
        public double Durable { get; [UsedImplicitly] init; } = 0.5;
        public double NotAQuestion { get; [UsedImplicitly] init; } = 0.5;
    }

    // A cosine cluster larger than this is judged on the members nearest its centroid; the rest
    // wait for a later pass. Twelve is 66 pairs, one request.
    public sealed record PairSettings
    {
        public int MaxClusterMemories { get; [UsedImplicitly] init; } = 12;
    }
}