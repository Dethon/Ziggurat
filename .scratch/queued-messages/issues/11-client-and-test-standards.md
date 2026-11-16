# 11: The page's queue code and the queue E2Es read as the rest of the repo does

**What to build:** No behaviour change except the chip's icon. Clear the review's client and test findings.

**Blocked by:** 09.

**Status:** done (the queue E2Es were written, not run)

- [x] A queue chip draws its file count with a stroked paperclip, so one chip never mixes the filled and stroked families (`blazor-client-ui.md`).
- [x] Words given back to the composer reach it by an action of the composer's own, not by riding dictation's `PendingTranscript`.
- [x] `QueueEffect` and `ReturnedMessagesEffect` share one catch-up trigger (topic opened, live again); `StreamingService`'s two send paths build their outgoing message in one place.
- [x] A topic open reads the queue once, not twice.
- [x] `McpAgent` asks whether options are the group's in one place.
- [x] The three queue E2E classes share their helpers (send, open a topic past an overlay, user message locator, idle).
- [x] An E2E pins a second *person* seeing another member's queued chip by name with no Edit or Remove (ticket 04 covered only a second device).
- [x] New comments in the touched client files say why, never what.
- [x] Client tests green under two concurrent processes; the queue E2E collection green. *(Client half only: the E2Es were edited and written, not run, since another checkout shares the stack's image tags.)*

**Notes from the implementation:**
- **The icon.** `Icons.PaperclipStroked`, Feather's paperclip (the set `Steer`, `Pencil` and `CloseStroked` come from) at the stroked default width; the composer keeps the filled `Paperclip`. `QueueChipsTests` reads the chip's markup and fails on any filled glyph there; it was red on the filled paperclip.
- **Words handed to the composer.** `OfferComposerText` (a fork's words, a send that went nowhere) and `ReturnToComposer` now write `ComposerState.Handed` (`HandedWords`: text, stamp, placement), not `Dictation.Transcript`; `TextPlacement` moved with them, and `PendingTranscript` is the dictation's alone again. `ChatInput` applies each slot by its own stamp, so a dictation's status change never re-applies handed words. The existing tests changed only their accessor; `ComposerDictationTests.WordsHandedToTheComposer_LeaveTheDictationsTranscriptAlone` pins the split. `ChatInput.razor` has no unit coverage: the two flows through it, a returned message landing before what is typed and a fork's words after it, are covered only by the E2Es, not run here.
- **One catch-up trigger.** `ShownTopicCatchUp` (topic opened, live again on the shown topic) is what `QueueEffect` and `ReturnedMessagesEffect` both subscribe to.
- **The queue read once on open.** `StreamResumeService` no longer asks for the queue of the topic on screen: the open (and the return to live) already asked. A topic resumed behind the shown one is still asked by the resume, and a stream starting on the shown topic asks nothing, its page being live for the pushes. Red first (`QueueCatchUpTests.OpeningATopicMidReply_AsksForItsQueueOnce`: two reads). Every `SelectTopic` comes after the first connect (the reopen waits for `TopicsLoaded`), so no open depended on the resume's read.
- **Outgoing message.** `StreamingService.Compose` builds both sends' `Outgoing`; a retry's own files still mean none from the composer.
- **McpAgent.** `AreTheAgentsToBuild(options)` (none, or the group's `TurnRunOptions`) answers both sites.
- **E2E.** `QueueE2E` holds the three classes' helpers: send, hold a reply on an approval (the retry loop), open a topic past the overlay by a dispatched click, the user-message locator, idle, the conversation as drawn, the provider wait and a second person's context. `StopReturnsQueueE2ETests` keeps its dispatched clicks. The new `QueuedMessagesE2ETests.AMembersQueuedMessage_ShowsOnAnotherMembersPage_NamedAndWithoutActions` follows the stop test's sequence for the second member (share by a dispatched click, open past the overlay) and ends on a dispatched Stop, so nothing queued costs a reply. None of the three was run.
- `.claude/rules/channels.md`'s queue paragraph names the shared trigger, the resume's narrower read and the composer's slot.
