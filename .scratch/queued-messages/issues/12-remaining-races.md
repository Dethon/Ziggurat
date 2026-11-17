# 12: A steered, stopped, raced or unshared message is shown once and never lost

**What to build:** Close the open edges tickets 05 to 09 recorded.

**Blocked by:** 10, 11.

**Status:** in-progress

- [x] **Stop just after the agent started a steer.** The steer must be either returned or stored, never both: the stop's cancel tells the agent which turn keys it returned, and the agent keeps no stored turn under a returned key (agent + hub seams).
- [x] **A steer is visible while it waits.** Between Steer now and the running turn's end, the steered message shows as a chip marked as steering, with no Edit or Remove, on every page of the topic, until it is drawn (hub push + client).
- [ ] **A send racing a stop is shown once.** A message the page drew as sent that the hub then returns to the composer is taken off the page (and likewise a refused send in a race with a resume: not drawn as sent, its words back in the composer).
- [ ] **A person's own first send still being vouched for when Stop lands** is not emitted after the cancel: it is returned to its composer like a queued message.
- [x] **A fire attaching mid-turn** pushes the queue, so no page offers Steer now on a turn that is not the reader's.
- [ ] **Unshare (user's decision, 2026-11-16).** Unsharing pulls a member's queued messages out of the queue; those and any returned words the member had not taken go to that member's open page, into whatever composer is on screen, with a toast saying the topic is no longer shared. A member with no page open gets them on their next page load.
- [ ] **Attachments are the sender's.** A queued or sent message naming file ids the sender does not hold is refused.
- [ ] **A refusal that is not the last message** still offers Retry.
- [x] **The per-chunk steer check** does not take the stream lock on every chunk when no steer waits.
- [x] **The steer E2E proves the cut:** it fails with the `Terminate` line removed (mutation-checked once and recorded).
- [ ] Hub, monitor and client filters green under two concurrent processes; the queue E2E collection green.

**Notes from the implementation (part A):**
- One commit per item: the fire's push (`c81f09c0e`), the steering chip (`b6de3bae8`), the unlocked per-chunk check (`378081719`), and the stop after a steer started (`736f35d79`). The E2E mutation check changed no code.
- **A fire attaching mid-turn** (red first). `GetOrCreateStream` clearing a non-null `RunningFor` now bumps the revision; without the bump the page's reducer drops the push. The push goes out through a new channel, `StreamService.QueueChanges`: a `QueueChange` carries the topic and its `ChannelSession`. The dispatcher reads it beside `Dispatches` and tells it through `QueueNews.TellOrLogAsync(QueueChange)`. So `create_conversation` needs no `QueueNews`, and the stream service still writes but never tells. The test drives the real `CreateConversationTool.McpRun`.
- **The steering chip** (red first; hub and client).
  - On the wire it is `QueueChangedNotification.Steering`, a list beside `Items`. A page from before, which knows only `Items`, never draws Edit or Remove on a message that has left the queue.
  - `QueueSnapshot` carries it, and `QueueOf().Messages` is still the waiting list alone, which `SteerQueued` looks the caller's message up in.
  - Revisions are bumped and told wherever the steers change:
    - `StartSteers` bumps and tells through `QueueChanges` as each steer is drawn.
    - The stop's cleanup bumps when steers were waiting. Before, a stop with only a steer pushed the same revision and the chip stuck (`AStopWithOnlyASteerWaiting_…`).
    - A refused steer is told again, since an `Unsteer` may follow a push that showed it.
  - The client keeps it as `TopicQueue.Steering`, drawn first as `QueueChip.Steering` with no actions and an `Icons.Steer` marker. A success dispatches `QueuedMessageSteered`, which moves the chip into the steering list instead of letting it go and come back.
  - `SteerNowE2ETests` now expects the chip marked steering, with no action buttons, until the approval lets the turn end, and then gone.
- **The per-chunk check** (red first, all four rows).
  - `HeldForSteer` takes `_streamLock` only for a chunk whose key is in `_waitingSteerKeys`. That set is a `ConcurrentDictionary`, written under the lock wherever a steer starts or stops waiting (`Steer`, `StartSteers`, `Unsteer`, the cleanup a stop or a stream's end runs) and read without it. The read is safe because a steer's answer can only arrive after its key was added: the steer is sent after that.
  - `StreamServiceSteerCheckTests` holds the lock with another topic's `Enqueue`, which reads a gated clock under it, and checks that a keyed chunk is written while that lock is still held: with no steer waiting, and after a steer was drawn, put back or stopped. The last three pin the exits, since the steer's own reply is every chunk under its key.
  - The first version of the test passed red: the gate's own 60 s timeout let the lock go inside `Eventually`'s 60 s budget. It now asserts the holder is still blocked and the gate waits 5 minutes.
- **Stop after a steer started** (red first on all three seams; the agent row where nothing was handed back pins existing behaviour).
  - Hub: `StopAsync` hands its cancel callback what it returned, and `CancelTopic` sends every returned id as `ChannelCancelNotification.ReturnedTurnKeys`. Only the steers' ids were ever sent as turn keys; the queued ones are harmless.
  - The field is optional both ways. `McpChannelConnection` maps it onto `ChannelMessage.ReturnedTurnKeys`, and a payload without it, or with a field it does not know, still cancels (pinned).
  - Agent: the group records the keys before `CancelOwnContext()` in a set kept for the group's life. Each turn's `TurnSteering` is built with its message's `TurnKey` and asks that set (`IsReturned`) at keep time, not at cancel time, so a steer dequeued but not yet set as running is covered too. `McpAgent` hands the handle to `RedisChatMessageStore.BeginCollecting`, and `KeepStoppedAsync` keeps nothing for a returned turn (the turn-in-flight mark is still cleared).
  - The row where nothing was handed back passed on its first run. It goes red when `IsReturned` is mutated to ignore the key (true whenever any key came back).
  - The real wire path was read rather than run: `channel_receive` serializes an inbox item whole, and `McpChannelConnection` serializes `item.Cancel` whole before mapping it. Nothing projects fields by hand. The receive, protocol, `Domain.Channels` and `Mcp.Hosting` suites are green (161).
  - Still open: a steer's turn that was stored before the cancel reached the agent. The likely case is one that fails fast and is kept as a failed turn. The hub still hands the steer back, so its sender gets it twice. Whether `send_reply` keeps one conversation's replies in order across turns, which would keep that window small, was not verified.
- **The E2E proves the cut.** With `context.Terminate = true;` (`ToolApprovalChatClient`, the steer's, not the rejection's) removed, `SteerNowE2ETests` was run once on images the fixture rebuilt for the mutant (145 s build). It failed on `said.ShouldNotContain(… AFTER{tag} …)`: the model was asked again after its tool and said the word. It had passed the new steering-chip assertions before that. The line was restored, and the collection run below is on images rebuilt again. The proof still depends on the model obeying "say AFTER after the tool", and it did, so the test was not strengthened.
- **Verified (part A).** The hub, monitor, client, agent and composition filter passed 1599/1599 in each of two concurrent processes. The queue E2E collection passed 4/4 on images the fixture rebuilt after the mutation was restored. The last box stays open for part B.

**Not fixed, by design:** an installed page from before the queue cannot take returned words (its code is old); a queued turn that started just before a stop is the turn the stop cancels; a fire already waiting in the agent's queue runs before a steer (spec's known edge).
