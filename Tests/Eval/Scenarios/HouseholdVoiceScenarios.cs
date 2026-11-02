using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Voice is one more channel of a household's, and what answers it is the household's voice agent:
// the agent an owner adds from the Nabu template, or any agent of theirs they mark — a Jonas as well
// as a Nabu. The voice scenarios are the home's Nabu's; these run the same turns on the template's
// own agent, and on a Jonas serving the satellite, which reads the voice rules only because the
// conversation was opened on voice (PromptManifest.ForChannel). Same contract, same checks: a
// household's satellite owes its person the same short spoken answer whoever is behind it.
public static class HouseholdVoiceScenarios
{
    private const string Template = ShippedDefinition.TemplatePrefix + "nabu";

    public static IReadOnlyList<Scenario> All =>
    [
        .. Spoken(Template, "the Nabu template's agent", canaries: true,
        [
            VoiceScenarios.ActionConfirmedWithItsValue, VoiceScenarios.TimeLeftIsTheValueAlone,
            VoiceScenarios.WhatDoesNotExistIsOneClause, VoiceScenarios.StopTheRingingAlarm,
            VoiceScenarios.TwoMoreMinutesAfterItRang, VoiceScenarios.FiveMinutesForTea,
            VoiceScenarios.TheThermostatInWords, VoiceScenarios.TheRaffleByVoice
        ]),
        // The home's Jonas has no Home Assistant, so the turns that need the home are left to the
        // template, whose agent reaches it.
        .. Spoken("jonas", "a Jonas answering aloud", canaries: false,
        [
            VoiceScenarios.ActionConfirmedWithItsValue, VoiceScenarios.TimeLeftIsTheValueAlone,
            VoiceScenarios.StopTheRingingAlarm, VoiceScenarios.TwoMoreMinutesAfterItRang,
            VoiceScenarios.FiveMinutesForTea, VoiceScenarios.TheRaffleByVoice
        ])
    ];

    // The family's one smoke canary is the template's: the voice family's own, run on it.
    private static IEnumerable<Scenario> Spoken(
        string agentId, string who, bool canaries, IEnumerable<Scenario> scenarios) =>
        scenarios.Select(s => s with
        {
            Name = $"{s.Name}, by {who}",
            AgentId = agentId,
            Tier = canaries ? s.Tier : EvalTier.Full
        });
}