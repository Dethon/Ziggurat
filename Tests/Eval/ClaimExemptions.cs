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
        [TimerPrompt.DurationIsACountdown.Id] =
            "Demonstrated on 2026-08-18 and it stayed green: with only the timers server hosted there is "
            + "nowhere else a countdown could go, so nothing here discriminates a timer from a calendar "
            + "alarm or a scheduled task. It needs the other two mounts — ticket 07.",
        [TimerPrompt.VoiceTargetsTheSpeakingRoom.Id] =
            "Demonstrated on 2026-08-18 and it stayed green: with the rule deleted from both the prompt "
            + "and the mount description, the model still targeted the room the decorated turn named. "
            + "Targeting the speaking room is the model's default on this turn shape, so a scenario that "
            + "witnesses the rule has to be one where the default and the rule disagree — ticket 07.",
        [TimerPrompt.AgentActsIsAScheduledTask.Id] =
            "Needs the scheduling server in process beside the timers server — ticket 07.",
        [TimerPrompt.ClockTimeIsACalendarAlarm.Id] =
            "Needs the calendar reachable through the Home Assistant fake — ticket 07.",
        [TimerPrompt.SchedulesAreNeverHumanReminders.Id] =
            "The negative half of the three-way discrimination; arrives with the scheduling server — ticket 07.",
        [TimerPrompt.DurationCappedAtFourHours.Id] =
            "The over-ceiling case creates a calendar entry, so it needs the Home Assistant fake — ticket 07.",
        [TimerPrompt.NoSatelliteAsksWhichRoom.Id] =
            "A turn that must create nothing and ask instead; it asserts on the reply, so it waits on the reply checks — ticket 07 with 09.",
        [TimerPrompt.TextIsSpokenNeverAnInstruction.Id] =
            "Discriminates a spoken message from an instruction, which is the scheduled-action family — ticket 07.",
        [TimerPrompt.SpokenStatusGivesOnlyTheRemainingTime.Id] =
            "A claim about what the reply says, not about which tool ran — ticket 09.",
        [TimerPrompt.RecreationIsNeverNarrated.Id] =
            "A claim about what the reply must not say — ticket 09.",
        [TimerPrompt.ListedByGlob.Id] =
            "Covered incidentally as a permitted call today; a scenario whose subject is listing is not written yet.",
        [TimerPrompt.CancelledByRemovingIt.Id] =
            "Cancelling is the delete half of the extend scenario; a cancel-only scenario is not written yet.",
        [TimerPrompt.RingingIsStoppedByDismiss.Id] =
            "Needs a turn arriving with a dismissed alert on it, which the voice family sets up — ticket 09.",
        [TimerPrompt.IdIsDescriptive.Id] =
            "What makes an id descriptive is a judgement about a word, which the deterministic checks cannot make; it waits on the judge pass.",
        [TimerPrompt.WrittenStatusIncludesFiresAt.Id] =
            "A claim about a written reply, and every scenario in the suite so far is a voice turn — ticket 09.",
        [TimerPrompt.ExtendingADismissedOneIsANewTimer.Id] =
            "Needs a turn arriving with a dismissed alert on it, and a timer that has already fired — ticket 09."
    };
}