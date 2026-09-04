using Domain.Prompts;

namespace Tests.Eval;

// Why a claim has no citing scenario, as a kind the scorecard can count. The kind is the triage:
// what it costs to close the entry is different for each, and an untyped list made the backlog
// look like one undifferentiated pile. Only kinds with entries live here — a deleted kind
// returns with its first user, and git history keeps its prose.
public enum ExemptionKind
{
    // Nothing blocks a scenario — the fixtures can witness it — but nobody has written one. The
    // cost is authoring plus the armed runs that validate it.
    Unwritten,

    // Not falsifiable as written, or deliberately not required — requiring it would test a habit
    // rather than an outcome.
    Unfalsifiable
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
        [WebBrowsingPrompt.NoProbeCalls.Id] = new(ExemptionKind.Unfalsifiable,
            "Written on 2026-08-20 against an observed quirk: on Home Assistant voice turns the "
            + "model sometimes opens with a minimal canary against example.com — a web_search for "
            + "'site:example.com' in one run, a web_browse of https://example.com at maxLength 1 "
            + "in another, roughly one snooze run in three — before doing the task. Every "
            + "scenario's exhaustive permitted set already fails such a call as unnecessary, so "
            + "the rule is guarded wherever it matters; no single scenario can own it, because no "
            + "scenario's subject is the probe itself. Production transcripts hold no example.com "
            + "call at all (every thread on the prod host, checked 2026-08-20), so the probe is "
            + "an artifact of the eval's fresh-context condition — worth the retry it "
            + "occasionally costs, not more prose."),
        [HomeAssistantPrompt.ThePastIsReadFromHistory.Id] = new(ExemptionKind.Unwritten,
            "Written on 2026-09-04 with the action. A scenario needs the fake home to keep a past — "
            + "its history endpoint answers every window empty — and a turn that asks about one, "
            + "e.g. a glucose sensor's night, judged on whether the answer came from history.sh "
            + "rather than state.json."),
        [VoicePrompt.OneSentenceTwelveWords.Id] = new(ExemptionKind.Unwritten,
            "Every spoken scenario declares a sentence and word limit, but none of them is *about* the "
            + "limit — the assertion is diffuse and no single scenario owns it — and the declared word "
            + "count is the contract's twelve plus what the spelled-out numbers exclusion is worth: the "
            + "check counts every word. A scenario whose subject is the limit needs a turn that tempts "
            + "a long answer."),
        [VoicePrompt.NothingIsNarrated.Id] = new(ExemptionKind.Unwritten,
            "Checked negatively wherever a scenario names what the reply must not say — a diffuse "
            + "assertion no single scenario owns — but nothing checks the general rule: a scenario "
            + "about it needs a turn whose work is worth narrating."),
    };
}