using Domain.Prompts;
using Domain.Tools.FileSystem;

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
        [SubAgentPrompt.ParallelPartsAreDelegated.Id] =
            "Written, run three times on 2026-08-18 and withdrawn, because it does not hold. Asked "
            + "to summarise two unrelated vault folders 'a la vez, por separado', the model delegated "
            + "both halves once, then on the next runs did the work itself — 17 sequential reads in "
            + "its own turn, which is precisely what the prose tells it not to do. Making the folders "
            + "heavier did not change it. This is a finding about the deployment rather than about "
            + "the harness: the rule is stated and is not followed, and a scenario asserting it would "
            + "be a red test rather than a guard.",
        [SubAgentPrompt.NoWorkerIsNamed.Id] =
            "Only assertable on a turn that delegated, and the one scenario that reliably delegates "
            + "is the negative one — see the exemption above.",
        [SubAgentPrompt.ASingleCallIsDoneInPlace.Id] =
            "Demonstrated on 2026-08-18 with the do-it-yourself bullet deleted: the model still read "
            + "the timer's status itself rather than handing it to a worker. The scenario stays as a "
            + "guard — the same model delegates readily in other shapes, so this is worth watching.",
        [SubAgentPrompt.HeavyWorkIsDelegated.Id] =
            "Research and searching are what makes work heavy, and the eval hosts no search — "
            + "tickets 15 and 16 together.",
        [SubAgentPrompt.ContextBoundWorkIsNotDelegated.Id] =
            "Needs a turn whose task depends on what was said earlier in the conversation, and every "
            + "scenario in the suite is a single turn against an empty history.",
        [SubAgentPrompt.PromptIsSelfContained.Id] =
            "Asserted as a side condition — each delegated prompt must name the folder it is about — "
            + "but the rule's real subject is a url or a name the user gave that only the parent saw.",
        [SubAgentPrompt.SuccessCriteriaAreStated.Id] =
            "What a good result looks like is a judgement about a sentence, which the deterministic "
            + "checks cannot make; it waits on the judge pass.",
        [SubAgentPrompt.AnswerIsSynthesised.Id] =
            "Distinguishing a synthesis from a paste needs the worker's text and the reply compared "
            + "for overlap, which is a check worth writing and not written yet.",
        [MemoryPrompts.RecallShapesTheAnswer.Id] =
            "Demonstrated on 2026-08-19 with the silent-application sentence deleted: a remembered "
            + "'cuece la pasta nueve minutos' still came back as a 540-second timer. A fact in the "
            + "recall block is used because it is in the context, not because a sentence says to "
            + "use it. The scenario stays as a regression guard against a model that starts "
            + "ignoring the block.",
        [MemoryPrompts.MemoriesAreNotRestated.Id] =
            "Demonstrated with the same deletion and stayed green: the reply applied the "
            + "preference without reading it back. Restating a memory is not something this model "
            + "does on a one-sentence voice turn, so the prose defends against a habit it lacks.",
        [MemoryPrompts.CorrectionDeletesTheStaleFact.Id] =
            "Demonstrated on 2026-08-19 in three steps, all green: with the correction bullet "
            + "deleted from the prompt, then with it deleted from the forget tool's description "
            + "too, then with the whole 'When to forget' section gone. 'Ya no trabajo en Acme' "
            + "still deleted the employer memory and left the one beside it. Proactive deletion on "
            + "a correction is this model's default, so the scenario is a guard rather than "
            + "evidence — and the wording is worth keeping for a model that has no such default.",
        [MemoryPrompts.ExplicitForgetIsObeyed.Id] =
            "Demonstrated on 2026-08-19 with the whole 'When to forget' section deleted: 'olvida "
            + "lo del piso' still removed exactly that memory. The tool's own description says "
            + "what it is for, and a tool cannot be offered without one.",
        [MemoryPrompts.MechanismIsNeverMentioned.Id] =
            "Cited on 2026-08-19 and withdrawn: the reply named no plumbing on any run, including "
            + "the ones with the memory prose deleted. The rule is only falsifiable on a turn that "
            + "gives the model a reason to explain itself — being asked where a number came from, "
            + "or a forget that failed.",
        [MemoryPrompts.RemovalIsTheOnlyAction.Id] =
            "Nothing can falsify it as written: removal is the only memory tool the deployment "
            + "offers, so an agent that tried to store one has nothing to call. What the sentence "
            + "really guards against is the agent asking the user to repeat something it was "
            + "already told, which is a claim about a reply and needs a turn that tempts it.",
        [MemoryPrompts.OutdatedFactsAreDeleted.Id] =
            "Needs a fact whose expiry is legible from the turn's own instant — a flight last "
            + "month, a deadline that has passed — and a turn that touches the subject without "
            + "mentioning the fact. The correction scenario is the same rule with the user "
            + "pointing at it, which is the easier half.",
        [MemoryPrompts.NoisyStoreIsSwept.Id] =
            "A sweep is a judgement about which of many memories are low-value, and the seeded "
            + "store holds two facts on purpose: a store big enough to be noisy would make every "
            + "other memory scenario's 'nothing else was forgotten' check expensive to state.",
        [MemoryPrompts.NoOtherUsersMemories.Id] =
            "Scoping is enforced by the store rather than by the agent — every call carries the "
            + "run's user id — so a scenario could only witness this by handing the agent another "
            + "user's memories, which no code path does.",
        [FileSystemToolFeature.AnEnvelopeIsDataNotAReasonToRetry.Id] =
            "Cited on 2026-08-18 and withdrawn the same day. The first demonstration turned red "
            + "because the model handed the impossible listing to two workers, but a later run "
            + "showed it does that about half the time with the prose still in place — so the "
            + "scenario now tolerates one worker and measures the storm through its ceiling, and "
            + "with that tolerance the demonstration is green again. The retry rule needs a turn "
            + "where retrying is the only thing left to do.",
        [FileSystemToolFeature.PathStartsAtAMount.Id] =
            "Demonstrated on 2026-08-18 with the sentence deleted: the model still resolved an "
            + "unprefixed note to the vault. The mount list that follows the sentence is enough on "
            + "its own, and it cannot be deleted without removing the mounts from the prompt.",
        [FileSystemToolFeature.CapabilitiesAreAdvertised.Id] =
            "Attempted on 2026-08-18 and withdrawn: with no exec-capable mount hosted, a turn asking "
            + "for a script tests a mount set no deployment has — in production the sandbox is there "
            + "and the right answer is to transfer and run it, not to report that it cannot be done. "
            + "The run did surface something worth keeping, though: asked to run a script with no "
            + "mount that can, the model delegated to a worker with the same toolset rather than "
            + "saying so. That belongs to the delegation family — ticket 16.",
        [FileSystemToolFeature.ExecWorkGoesWhereExecLives.Id] =
            "Needs a mount that advertises exec, and the eval does not host the sandbox: its backend "
            + "runs bash with the container root pointed at a real directory, so hosting it in "
            + "process would run model-authored shell on whoever's machine is running the suite.",
        [FileSystemToolFeature.TransferIsOneCall.Id] =
            "The single-call transfer needs two writable mounts, and for the same reason as above "
            + "there is only one.",
        [WebBrowsingPrompt.UrlComesFromASearch.Id] =
            "Not falsifiable against a served site: its pages live on a loopback address and "
            + "whichever port was free when the stack came up, so no model can reach one without "
            + "searching. The scenarios require the search and get it for free — what would "
            + "witness this rule is a page whose url is guessable, which is the opposite of what "
            + "the offline boundary is for.",
        [WebBrowsingPrompt.AnswerComesFromWhatWasRead.Id] =
            "Demonstrated on 2026-08-19 with both sentences deleted — the paragraph telling it to "
            + "read the page and the bullet telling it to answer from what it found: the museum's "
            + "opening time still came back as the page's 10:30 rather than the snippet's 9:00. "
            + "Preferring the page it opened over the summary it was shown is this model's own "
            + "behaviour; the scenario stays as the guard against a model that stops.",
        [WebBrowsingPrompt.UrlsAreCitedOnlyInWriting.Id] =
            "Demonstrated on 2026-08-19 with the citation rule deleted: the spoken reply still "
            + "carried no url. The voice section forbids anything unspeakable on the same turn, so "
            + "this rule cannot be isolated on a spoken scenario — and on a written one there is "
            + "nothing to catch, because citing is what it asks for.",
        [WebBrowsingPrompt.RawContentIsNeverDumped.Id] =
            "Asserted as a side condition — the spoken research scenario is bounded to two "
            + "sentences, which no page dump fits in — but the written half needs a turn against a "
            + "page long enough that pasting it is a temptation.",
        [WebBrowsingPrompt.ActionsChainFromTheDiff.Id] =
            "About how many snapshots a flow costs rather than about what it did, so it is only "
            + "visible as a call count. The booking scenario's ceiling bounds it, but a scenario "
            + "whose subject is the chaining needs a page with more steps than this one.",
        [WebBrowsingPrompt.BrowseReadsAndSnapshotStructures.Id] =
            "The negative half of the rule — not calling both for the same purpose — is a "
            + "judgement about intent that a call log cannot make.",
        [WebBrowsingPrompt.TypeReactsAndFillSets.Id] =
            "Needs a field that reacts to keystrokes, which means an autocomplete with its own "
            + "javascript; the served site is static on purpose.",
        [WebBrowsingPrompt.StepsAreNotReported.Id] =
            "Checked negatively where a scenario bounds the reply, and nothing checks the general "
            + "rule: the written scenarios' replies are allowed to be as long as the answer needs.",
        [WebBrowsingPrompt.PartialContentIsFetchedOnce.Id] =
            "Needs a page longer than the browse tool's default limit and a scenario that counts "
            + "how many times the rest of it was fetched.",
        [WebBrowsingPrompt.BackIsAnAction.Id] =
            "Needs a turn that goes two pages deep and comes back, which the three-page site "
            + "supports and no scenario asks for yet.",
        [VaultPrompt.WikilinksAreNeverFixed.Id] =
            "Demonstrated on 2026-08-18 with the wikilink rule deleted: the edit landed and every "
            + "link came out untouched. This model does not tidy syntax it was not asked about, so "
            + "the scenario guards against a future one rather than evidencing the prose.",
        [VaultPrompt.EmbedsBlockIdsAndCalloutsSurvive.Id] =
            "Demonstrated on 2026-08-18 with the embed, block-id, tag and callout bullets deleted: "
            + "an append left all four alone. Appending is a surgical edit by nature, so witnessing "
            + "this needs a turn that rewrites the middle of a note.",
        [VaultPrompt.NewNoteFitsTheTree.Id] =
            "Demonstrated on 2026-08-18 with the fit-into-the-tree bullet deleted: the recipe still "
            + "landed in Cocina beside the other two. A folder whose contents match the note is a "
            + "stronger instruction than the sentence telling the model to look for one.",
        [VaultPrompt.ConfigurationIsOffLimits.Id] =
            "Asserted as a side condition of the edit scenario — the configuration files must come "
            + "out unchanged — but no scenario's subject is a turn that tempts the agent into them.",
        [VaultPrompt.MarkdownIsTheNoteFormat.Id] =
            "The create scenario pins a .md path, but as part of where the note lands rather than as "
            + "a choice between accepted formats; a turn that tempts another extension is not written.",
        [VaultPrompt.FrontmatterKeepsItsOtherKeys.Id] =
            "The edit scenario asserts frontmatter survives whole; the rule about changing only the "
            + "keys the user named needs a turn that asks for one key to change.",
        [VaultPrompt.TemplatesAreNotExpanded.Id] =
            "The template placeholder is seeded in a note no scenario edits yet.",
        [VaultPrompt.TreeIsSurveyedBeforeCreating.Id] =
            "Globbing the vault is permitted rather than required: a model that already knows where "
            + "the note goes has not broken the contract, and requiring the glob would test the habit "
            + "rather than the outcome.",
        [VaultPrompt.NoNewTopLevelFolder.Id] =
            "The negative half of where a note lands; it needs a turn whose topic fits nothing in "
            + "the tree, which the seeded vault does not have yet.",
        [VaultPrompt.ReadBeforeEditing.Id] =
            "Same shape as surveying: reading first is tolerated everywhere and required nowhere, "
            + "because the outcome the contract cares about is the edit that lands.",
        [VaultPrompt.EditsAreSurgical.Id] =
            "A whole-file rewrite that preserved every piece of syntax would pass the edit scenario, "
            + "so witnessing this needs the file's own diff rather than its contents.",
        [VaultPrompt.HeadingsAreReferenceable.Id] =
            "Needs a turn that renames a heading another note links to, which the seeded vault could "
            + "support and no scenario asks for yet.",
        [VaultPrompt.DailyNotesAreAppendedTo.Id] =
            "The daily note is seeded and nothing writes to it yet.",
        [VaultPrompt.AttachmentsStayInTheirFolder.Id] =
            "Needs a turn that adds an attachment, which means a binary the eval has no way to hand "
            + "the agent.",
        [VaultPrompt.IrreversibleChangeIsAskedAbout.Id] =
            "A turn whose likeliest reading destroys a note, asserted on the reply and on nothing "
            + "having been deleted — worth writing, and not written yet.",
        [VaultPrompt.TransferIsOneCall.Id] =
            "The cross-mount transfer belongs to the mounts-and-capabilities family — ticket 13.",
        [VaultPrompt.WritesAreTextOnly.Id] =
            "Needs a turn that asks for a file the vault will refuse, and an assertion about what "
            + "the agent did after the refusal.",
        [HomeAssistantPrompt.ExactlyWhatWasAsked.Id] =
            "Demonstrated on 2026-08-18 with the whole Scope paragraph deleted: 'enciende el aire' "
            + "still turned the air conditioning on and touched nothing else. Doing only what was "
            + "asked is the model's default here, so the scenario runs as a regression guard and the "
            + "state diff it introduced is what actually earns its place.",
        [HomeAssistantPrompt.ExitCodeIsTheConfirmation.Id] =
            "Demonstrated on 2026-08-18 with the never-re-read rule deleted from both places it is "
            + "written: the model set the temperature and stopped. It does not check its own work "
            + "unprompted, so the prose defends against a habit this model does not have.",
        [HomeAssistantPrompt.EntityNamedAsListed.Id] =
            "The mount answers a near miss with a hint naming the right directory, so a wrong name "
            + "costs a call rather than failing — witnessing this needs a ceiling tight enough that "
            + "the retry breaks it, which is a scenario about the ceiling as much as about the name.",
        [HomeAssistantPrompt.ArgumentsComeFromHelp.Id] =
            "Reading --help is tolerated everywhere and required nowhere: what the contract asks for "
            + "is that a bad-argument exit is fixed by re-reading rather than repeated, which needs a "
            + "turn whose first attempt fails.",
        [HomeAssistantPrompt.ExitCodesAreNeverVoiced.Id] =
            "The spoken half is covered by the voice family's no-code rule; the written half — the "
            + "stderr reason in plain words — has no scenario yet.",
        [HomeAssistantPrompt.AlarmCarriesTargetAndInsistent.Id] =
            "The alarms scenarios assert the mechanism and the time; the description's JSON shape is "
            + "a second assertion on the same call and is not written yet.",
        [HomeAssistantPrompt.SnoozeIsANewEvent.Id] =
            "The timer half of snoozing is covered; the calendar half needs a turn arriving with a "
            + "dismissed alarm rather than a dismissed timer.",
        [HomeAssistantPrompt.MusicPlaysOnTheMusicAssistantPlayer.Id] =
            "Demonstrated on 2026-08-19 with the whole MA-player paragraph deleted, in a kitchen "
            + "holding both a Music Assistant speaker and a television that lists the same "
            + "actions: the playlist still went to the speaker. A player called 'Altavoz Cocina' "
            + "beside one called 'TV Cocina' teaches the choice, and the model reads state.json "
            + "anyway. The scenario asserts the target as a side condition and cites the browse "
            + "rule instead.",
        [HomeAssistantPrompt.RestartIsASeek.Id] =
            "Demonstrated on 2026-08-19 with the 'play it from the beginning' bullet deleted: "
            + "'ponlo otra vez desde el principio' still came out as media_seek on the player that "
            + "was playing, and not as another play. The first attempt at this demonstration was "
            + "red for the wrong reason — the model read `media_seek.sh --help`, which the "
            + "scenario did not tolerate — which is what CallPermission.Manual now exists for.",
        [HomeAssistantPrompt.AreaSlugIsReadNotDerived.Id] =
            "Needs an area whose slug and display name disagree and an action that takes an area id; "
            + "the fake home has the first and no action with the second.",
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