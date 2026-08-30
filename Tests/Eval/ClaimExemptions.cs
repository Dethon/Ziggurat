using Domain.Prompts;

namespace Tests.Eval;

// Why a claim has no citing scenario, as a kind the scorecard can count. The kind is the triage:
// what it costs to close the entry is different for each, and an untyped list made the backlog
// look like one undifferentiated pile.
public enum ExemptionKind
{
    // A scenario runs and asserts the behaviour; it just cannot earn the citation, because the
    // prose was deleted and nothing changed. The rate lives in the scorecard's scenarios section.
    Guard,

    // Nothing blocks a scenario — the fixtures can witness it — but nobody has written one. The
    // cost is authoring plus the armed runs that validate it.
    Unwritten,

    // The harness or a fixture cannot witness it yet; the blocking work is named in the reason.
    NeedsFixture,

    // Not falsifiable as written, or deliberately not required — requiring it would test a habit
    // rather than an outcome.
    Unfalsifiable,

    // A judgement about a sentence or an intent, which the deterministic checks cannot make; it
    // waits on a judged check with a rubric.
    Judge,

    // The rule is stated and the deployment does not follow it: a scenario would be a standing
    // red rather than a guard. A finding about the deployment, not a backlog item — and one that
    // closes by withdrawing the claim, which is how the two delegation findings went
    // (2026-08-20): the when-to-delegate bullets were ignored as prompt prose and again as the
    // subagent tool's own description, the behaviour is adequate either way, so the bullets stay
    // as prose the eval no longer holds the model to. The story lives with the claims in
    // SubAgentPrompt.
    Finding
}

public sealed record Exemption(ExemptionKind Kind, string Reason);

// The backlog, in the open: the claims no scenario touches at all — a claim a scenario asserts
// without evidencing belongs on that scenario as a guard, not here. What is left is guarded only
// diffusely, by a property every scenario (or every spoken one) declares, and owned by none.
//
// Removing a line here is what adding a scenario looks like.
public static class ClaimExemptions
{
    public static IReadOnlyDictionary<string, Exemption> Reasons { get; } = new Dictionary<string, Exemption>
    {
        [WebBrowsingPrompt.NoProbeCalls.Id] = new(ExemptionKind.Guard,
            "Written on 2026-08-20 against an observed quirk: on Home Assistant voice turns the "
            + "model sometimes opens with a minimal canary against example.com — a web_search for "
            + "'site:example.com' in one run, a web_browse of https://example.com at maxLength 1 "
            + "in another, roughly one snooze run in three — before doing the task. Every "
            + "scenario's exhaustive permitted set already fails such a call as unnecessary, so "
            + "the rule is guarded wherever it matters; no scenario's subject is the probe "
            + "itself. Production transcripts hold no example.com call at all (every thread on "
            + "the prod host, checked 2026-08-20), so the probe is an artifact of the eval's "
            + "fresh-context condition — worth the retry it occasionally costs, not more "
            + "prose."),
        [VoicePrompt.OneSentenceTwelveWords.Id] = new(ExemptionKind.Guard,
            "Every spoken scenario declares a sentence and word limit, but none of them is *about* the "
            + "limit, and the declared word count is the contract's twelve plus what the spelled-out "
            + "numbers exclusion is worth — the check counts every word. A scenario whose subject is the "
            + "limit needs a turn that tempts a long answer."),
        [VoicePrompt.NothingIsNarrated.Id] = new(ExemptionKind.Guard,
            "Checked negatively wherever a scenario names what the reply must not say, but nothing "
            + "checks the general rule: a scenario about it needs a turn whose work is worth narrating."),
    };
}