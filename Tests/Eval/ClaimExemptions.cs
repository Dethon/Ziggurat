using Domain.Prompts;
using Domain.Tools.FileSystem;

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
    // red rather than a guard. A finding about the deployment, not a backlog item.
    Finding
}

public sealed record Exemption(ExemptionKind Kind, string Reason);

// The backlog, in the open. A claim here is one the prompt makes and nothing verifies, with the
// reason it is not verified yet — which is the point of declaring claims up front: the untested
// rules are the ones most worth writing down, and a list nobody can see is an assumption again.
//
// Removing a line here is what adding a scenario looks like.
public static class ClaimExemptions
{
    public static IReadOnlyDictionary<string, Exemption> Reasons { get; } = new Dictionary<string, Exemption>
    {
        [SubAgentPrompt.ParallelPartsAreDelegated.Id] = new(ExemptionKind.Finding,
            "Written, run three times on 2026-08-18 and withdrawn, because it does not hold. Asked "
            + "to summarise two unrelated vault folders 'a la vez, por separado', the model delegated "
            + "both halves once, then on the next runs did the work itself — 17 sequential reads in "
            + "its own turn, which is precisely what the prose tells it not to do. Making the folders "
            + "heavier did not change it. This is a finding about the deployment rather than about "
            + "the harness: the rule is stated and is not followed, and a scenario asserting it would "
            + "be a red test rather than a guard."),
        [SubAgentPrompt.HeavyWorkIsDelegated.Id] = new(ExemptionKind.Finding,
            "Written three ways on 2026-08-19 and withdrawn as a citation, because only half of "
            + "it holds. On a light research turn (one page's opening hours) the model rightly "
            + "did the work in place, three runs of three. On the heavy turn (summarise the "
            + "thirty-thousand-character chronicle) it delegated readily — every delegating run "
            + "wrote a self-contained worker prompt with success criteria — and then did the "
            + "research itself anyway: after the worker answered, it searched, fetched the page "
            + "twice, and browsed the very url the worker had cited. 'Delegated rather than run "
            + "in the parent's own turn' is precisely the half that does not hold; the finding "
            + "on parallel-parts-are-delegated records the same distrust. Seven runs on "
            + "2026-08-20, after a trust-the-result rule was added to the prose, split four "
            + "delegating to three doing the work in place — same model, same served provider — "
            + "so whether this turn delegates is a per-run coin. The research scenario now "
            + "asserts the once-paid outcome instead of the choice, and the prompt-quality "
            + "claims it cited moved to this backlog."),
        [SubAgentPrompt.TheResultIsNotRedone.Id] = new(ExemptionKind.Guard,
            "Written on 2026-08-20 against the distrust the two findings above record: nine armed "
            + "runs of the research scenario showed the model delegating and then searching, "
            + "fetching the page twice, and browsing the very url its worker cited. The research "
            + "scenario's ceiling now leaves no room for that wholesale redo, but a partial one — "
            + "a single stray search after delegating — still fits under it, so the ceiling "
            + "guards rather than cites. A citation needs a conditional expectation (if a "
            + "delegation happened, no web call may follow it) the harness cannot express yet."),
        [SubAgentPrompt.PromptIsSelfContained.Id] = new(ExemptionKind.NeedsFixture,
            "Held in every delegating run — each wrote a self-contained worker prompt — but "
            + "whether the research turn delegates at all is a per-run coin (see "
            + "heavy-work-is-delegated), so a scenario requiring the delegation flakes on the "
            + "runs that do the work in place. Citing this again needs a conditional expectation "
            + "the harness cannot express yet: if a delegation happened, its prompt must carry "
            + "the site and the subject."),
        [SubAgentPrompt.SuccessCriteriaAreStated.Id] = new(ExemptionKind.NeedsFixture,
            "Same blocker as prompt-is-self-contained: the judged rubric needs a delegated "
            + "prompt as its material, and whether one exists is a per-run coin. Every "
            + "delegating run on 2026-08-19 and 2026-08-20 did state what a good result looks "
            + "like."),
        [SubAgentPrompt.AnswerIsSynthesised.Id] = new(ExemptionKind.NeedsFixture,
            "Same blocker: the rubric compares the reply against the worker's answer, which only "
            + "exists on the runs that delegate. Where it was graded, one run on 2026-08-20 was "
            + "failed for carrying the worker's facts in the worker's order — which a faithful "
            + "summary of a two-sentence canned answer cannot avoid; the rubric kept in the "
            + "scenario's history says so for whoever revives it."),
        [SubAgentPrompt.NoWorkerIsNamed.Id] = new(ExemptionKind.Guard,
            "The research scenario's reply forbids every worker word in both shapes, so the rule "
            + "is asserted on every run; it just cannot earn the citation, because on an "
            + "in-place run there is no worker to be tempted to name."),
        [SubAgentPrompt.ASingleCallIsDoneInPlace.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the do-it-yourself bullet deleted: the model still read "
            + "the timer's status itself rather than handing it to a worker. The scenario stays as a "
            + "guard — the same model delegates readily in other shapes, so this is worth watching. "
            + "Watching paid on 2026-08-20: a flake dump showed the invented-station scenario handing "
            + "the play action itself to a worker and voicing the canned 'Hecho' as success — a false "
            + "confirmation the reply checks alone could not catch. The single-action bullet in the "
            + "prose is the answer to that run."),
        [MemoryPrompts.RecallShapesTheAnswer.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-19 with the silent-application sentence deleted: a remembered "
            + "'cuece la pasta nueve minutos' still came back as a 540-second timer. A fact in the "
            + "recall block is used because it is in the context, not because a sentence says to "
            + "use it. The scenario stays as a regression guard against a model that starts "
            + "ignoring the block."),
        [MemoryPrompts.MemoriesAreNotRestated.Id] = new(ExemptionKind.Guard,
            "Demonstrated with the same deletion and stayed green: the reply applied the "
            + "preference without reading it back. Restating a memory is not something this model "
            + "does on a one-sentence voice turn, so the prose defends against a habit it lacks."),
        [MemoryPrompts.CorrectionDeletesTheStaleFact.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-19 in three steps, all green: with the correction bullet "
            + "deleted from the prompt, then with it deleted from the forget tool's description "
            + "too, then with the whole 'When to forget' section gone. 'Ya no trabajo en Acme' "
            + "still deleted the employer memory and left the one beside it. Proactive deletion on "
            + "a correction is this model's default, so the scenario is a guard rather than "
            + "evidence — and the wording is worth keeping for a model that has no such default."),
        [MemoryPrompts.ExplicitForgetIsObeyed.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-19 with the whole 'When to forget' section deleted: 'olvida "
            + "lo del piso' still removed exactly that memory. The tool's own description says "
            + "what it is for, and a tool cannot be offered without one."),
        [FileSystemToolFeature.AnEnvelopeIsDataNotAReasonToRetry.Id] = new(ExemptionKind.Guard,
            "Cited on 2026-08-18 and withdrawn the same day. The first demonstration turned red "
            + "because the model handed the impossible listing to two workers, but a later run "
            + "showed it does that about half the time with the prose still in place — so the "
            + "scenario now tolerates one worker and measures the storm through its ceiling, and "
            + "with that tolerance the demonstration is green again. The retry rule needs a turn "
            + "where retrying is the only thing left to do."),
        [FileSystemToolFeature.PathStartsAtAMount.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the sentence deleted: the model still resolved an "
            + "unprefixed note to the vault. The mount list that follows the sentence is enough on "
            + "its own, and it cannot be deleted without removing the mounts from the prompt."),
        [FileSystemToolFeature.CapabilitiesAreAdvertised.Id] = new(ExemptionKind.Guard,
            "The sandbox is hosted now, and the checksum scenario asserts this as a side condition: "
            + "an exec against a mount that does not advertise it is an unnecessary call there. A "
            + "scenario whose subject is the choice itself — tempted toward the wrong mount — is "
            + "not written."),
        [WebBrowsingPrompt.AnswerComesFromWhatWasRead.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-19 with both sentences deleted — the paragraph telling it to "
            + "read the page and the bullet telling it to answer from what it found: the museum's "
            + "opening time still came back as the page's 10:30 rather than the snippet's 9:00. "
            + "Preferring the page it opened over the summary it was shown is this model's own "
            + "behaviour; the scenario stays as the guard against a model that stops."),
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
        [WebBrowsingPrompt.RawContentIsNeverDumped.Id] = new(ExemptionKind.Guard,
            "Asserted as a side condition on both halves now: the spoken research scenario is "
            + "bounded to two sentences, and the chronicle scenario bounds a written reply to four "
            + "against a thirty-thousand-character page. No scenario's subject is the dumping "
            + "itself, so it guards rather than cites."),
        [VaultPrompt.WikilinksAreNeverFixed.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the wikilink rule deleted: the edit landed and every "
            + "link came out untouched. This model does not tidy syntax it was not asked about, so "
            + "the scenario guards against a future one rather than evidencing the prose."),
        [VaultPrompt.EmbedsBlockIdsAndCalloutsSurvive.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the embed, block-id, tag and callout bullets deleted: "
            + "an append left all four alone. Appending is a surgical edit by nature, so witnessing "
            + "this needs a turn that rewrites the middle of a note."),
        [VaultPrompt.NewNoteFitsTheTree.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the fit-into-the-tree bullet deleted: the recipe still "
            + "landed in Cocina beside the other two. A folder whose contents match the note is a "
            + "stronger instruction than the sentence telling the model to look for one."),
        [VaultPrompt.ConfigurationIsOffLimits.Id] = new(ExemptionKind.Guard,
            "Asserted as a side condition of the edit scenario — the configuration files must come "
            + "out unchanged — but no scenario's subject is a turn that tempts the agent into them."),
        [VaultPrompt.MarkdownIsTheNoteFormat.Id] = new(ExemptionKind.Guard,
            "The create scenario pins a .md path, but as part of where the note lands rather than as "
            + "a choice between accepted formats; a turn that tempts another extension is not written."),
        [VaultPrompt.TransferIsOneCall.Id] = new(ExemptionKind.Guard,
            "The checksum scenario transfers out of the vault with a single copy and permits no "
            + "create; what it cites is the mounts section's transfer rule, and the vault prompt's "
            + "own copy of the sentence rides along uncited."),
        [HomeAssistantPrompt.ExactlyWhatWasAsked.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the whole Scope paragraph deleted: 'enciende el aire' "
            + "still turned the air conditioning on and touched nothing else. Doing only what was "
            + "asked is the model's default here, so the scenario runs as a regression guard and the "
            + "state diff it introduced is what actually earns its place."),
        [HomeAssistantPrompt.ExitCodeIsTheConfirmation.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the never-re-read rule deleted from both places it is "
            + "written: the model set the temperature and stopped. It does not check its own work "
            + "unprompted, so the prose defends against a habit this model does not have."),
        [HomeAssistantPrompt.MusicPlaysOnTheMusicAssistantPlayer.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-19 with the whole MA-player paragraph deleted, in a kitchen "
            + "holding both a Music Assistant speaker and a television that lists the same "
            + "actions: the playlist still went to the speaker. A player called 'Altavoz Cocina' "
            + "beside one called 'TV Cocina' teaches the choice, and the model reads state.json "
            + "anyway. The scenario asserts the target as a side condition and cites the browse "
            + "rule instead."),
        [HomeAssistantPrompt.RestartIsASeek.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-19 with the 'play it from the beginning' bullet deleted: "
            + "'ponlo otra vez desde el principio' still came out as media_seek on the player that "
            + "was playing, and not as another play. The first attempt at this demonstration was "
            + "red for the wrong reason — the model read `media_seek.sh --help`, which the "
            + "scenario did not tolerate — which is what CallPermission.Manual now exists for."),
        [VoicePrompt.OneSentenceTwelveWords.Id] = new(ExemptionKind.Guard,
            "Every spoken scenario declares a sentence and word limit, but none of them is *about* the "
            + "limit, and the declared word count is the contract's twelve plus what the spelled-out "
            + "numbers exclusion is worth — the check counts every word. A scenario whose subject is the "
            + "limit needs a turn that tempts a long answer."),
        [VoicePrompt.NothingIsNarrated.Id] = new(ExemptionKind.Guard,
            "Checked negatively wherever a scenario names what the reply must not say, but nothing "
            + "checks the general rule: a scenario about it needs a turn whose work is worth narrating."),
        [TimerPrompt.DurationIsACountdown.Id] = new(ExemptionKind.Guard,
            "Demonstrated twice and stayed green both times, most recently on 2026-08-18 with all three "
            + "mounts hosted: deleting the countdown rule from the timer, Home Assistant and scheduling "
            + "prompts still left 'recuérdame en diez minutos' in /timers. The timers mount description "
            + "teaches it too, and deleting that takes the create path with it — so this claim cannot be "
            + "falsified by deleting prose. The scenario is kept as a regression guard against a model "
            + "that stops discriminating; it just cannot earn the citation."),
        [TimerPrompt.VoiceTargetsTheSpeakingRoom.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the rule deleted from the prompt and the mount, on a turn "
            + "where the words point at another room (pasta, asked from the office): the model still "
            + "targeted the office. Targeting the speaking room is its default whenever the decorated "
            + "turn names a room, so nothing short of removing the room from the turn discriminates."),
        [TimerPrompt.AgentActsIsAScheduledTask.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18: with the two-step choice deleted from the timer, scheduling and "
            + "Home Assistant prompts, 'apaga el aire dentro de una hora' still became a /schedules "
            + "one-shot at the right absolute time. The model works out on its own that a timer only "
            + "speaks, so the prose is redundant on this turn shape."),
        [TimerPrompt.ClockTimeIsACalendarAlarm.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the rule deleted from the timer prompt, the Home Assistant "
            + "prompt and the timers mount description: 'despiértame mañana a las siete' still went to "
            + "the alarms calendar. The calendar entity is called Assistant Alarms, which teaches the "
            + "same thing and cannot be deleted without deleting the calendar."),
        [TimerPrompt.SchedulesAreNeverHumanReminders.Id] = new(ExemptionKind.Guard,
            "Demonstrated on 2026-08-18 with the rule deleted from all three prompts: a ten-minute "
            + "reminder still went to /timers rather than /schedules. Nothing tempts the model into "
            + "/schedules for a human reminder, so the negative half of the discrimination has no "
            + "turn that witnesses it yet."),
    };
}