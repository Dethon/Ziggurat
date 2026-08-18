using Domain.Prompts;

namespace Tests.Eval;

// The backlog, in the open. A claim here is one the prompt makes and nothing verifies, with the
// reason it is not verified yet — which is the point of declaring claims up front: the untested
// rules are the ones most worth writing down, and a list nobody can see is an assumption again.
//
// Removing a line here is what adding a scenario looks like.
public static class ClaimExemptions
{
    public static IReadOnlyDictionary<string, string> Reasons { get; } = new Dictionary<string, string>
    {
        [VoicePrompt.OneSentenceTwelveWords.Id] =
            "Every spoken scenario declares a sentence and word limit, but none of them is *about* the "
            + "limit, and the declared word count is the contract's twelve plus what the spelled-out "
            + "numbers exclusion is worth — the check counts every word. A scenario whose subject is the "
            + "limit needs a turn that tempts a long answer.",
        [VoicePrompt.SeveralSentencesOnlyWhenAsked.Id] =
            "The three-sentence allowance applies to a turn that asked for an explanation, a comparison "
            + "or a list, and every scenario in the suite asks for one short thing.",
        [VoicePrompt.UnclearRequestIsActedOn.Id] =
            "Two rules in one: act on the likeliest reading, and ask before an irreversible delete. The "
            + "second needs a turn whose likeliest reading destroys something, which no family sets up yet.",
        [VoicePrompt.NothingIsNarrated.Id] =
            "Checked negatively wherever a scenario names what the reply must not say, but nothing "
            + "checks the general rule: a scenario about it needs a turn whose work is worth narrating.",
        [VoicePrompt.AbbreviationsAreSpelledOut.Id] =
            "Needs a turn whose answer is a unit or an acronym — a temperature, a distance — which "
            + "arrives with the Home Assistant family.",
        [VoicePrompt.OneWordBeforeSlowWork.Id] =
            "A claim about what is said *before* the work, which the recording cannot see: it holds one "
            + "reply, and the acknowledgement is a separate emission — tickets 15 and 16.",
        [TimerPrompt.DurationIsACountdown.Id] =
            "Demonstrated twice and stayed green both times, most recently on 2026-08-18 with all three "
            + "mounts hosted: deleting the countdown rule from the timer, Home Assistant and scheduling "
            + "prompts still left 'recuérdame en diez minutos' in /timers. The timers mount description "
            + "teaches it too, and deleting that takes the create path with it — so this claim cannot be "
            + "falsified by deleting prose. The scenario is kept as a regression guard against a model "
            + "that stops discriminating; it just cannot earn the citation.",
        [TimerPrompt.VoiceTargetsTheSpeakingRoom.Id] =
            "Demonstrated on 2026-08-18 with the rule deleted from the prompt and the mount, on a turn "
            + "where the words point at another room (pasta, asked from the office): the model still "
            + "targeted the office. Targeting the speaking room is its default whenever the decorated "
            + "turn names a room, so nothing short of removing the room from the turn discriminates.",
        [TimerPrompt.AgentActsIsAScheduledTask.Id] =
            "Demonstrated on 2026-08-18: with the two-step choice deleted from the timer, scheduling and "
            + "Home Assistant prompts, 'apaga el aire dentro de una hora' still became a /schedules "
            + "one-shot at the right absolute time. The model works out on its own that a timer only "
            + "speaks, so the prose is redundant on this turn shape.",
        [TimerPrompt.ClockTimeIsACalendarAlarm.Id] =
            "Demonstrated on 2026-08-18 with the rule deleted from the timer prompt, the Home Assistant "
            + "prompt and the timers mount description: 'despiértame mañana a las siete' still went to "
            + "the alarms calendar. The calendar entity is called Assistant Alarms, which teaches the "
            + "same thing and cannot be deleted without deleting the calendar.",
        [TimerPrompt.SchedulesAreNeverHumanReminders.Id] =
            "Demonstrated on 2026-08-18 with the rule deleted from all three prompts: a ten-minute "
            + "reminder still went to /timers rather than /schedules. Nothing tempts the model into "
            + "/schedules for a human reminder, so the negative half of the discrimination has no "
            + "turn that witnesses it yet.",
        [TimerPrompt.TextIsSpokenNeverAnInstruction.Id] =
            "Demonstrated on 2026-08-18 with the rule deleted from the timer and Home Assistant prompts: "
            + "the deferred action still became a schedule rather than a timer whose text is a command. "
            + "Witnessing this one needs a turn that forces a timer and then asks what goes in its text.",
        [TimerPrompt.ListedByGlob.Id] =
            "Covered incidentally as a permitted call today; a scenario whose subject is listing is not written yet.",
        [TimerPrompt.CancelledByRemovingIt.Id] =
            "Cancelling is the delete half of the extend scenario; a cancel-only scenario is not written yet.",
        [TimerPrompt.IdIsDescriptive.Id] =
            "What makes an id descriptive is a judgement about a word, which the deterministic checks cannot make; it waits on the judge pass."
    };
}