# 09: Review fixes: a turn end that is not the running turn's, and what the tests did not pin

**What to build:** Close what a code review of tickets 07 and 08 found.

**A turn end that is not the running turn's.** Ticket 08 made a reply end its turn when it says `IsComplete`. Two cases still counted the wrong turn as ended, so a waiting steer's message was written in the middle of a reply that was still streaming:
- **A steer the agent refuses as it arrives.** For example, its member was disabled meanwhile. The agent answers it with `Error` and then `StreamComplete` while the turn the steer was meant to cut is still running.
- **A fire that ends first.** A schedule's turn attached to the topic mid-turn completes before the person's turn does.

**The rest:**
- **A fixed sleep.** The Stop E2E bought an absence with a 10 s sleep.
- **An untested case.** `Transcript.Shown`'s reply stopped between tool calls had been checked only by reading the code.
- **An unchecked refusal.** A queued send's refusal yields a bare `Error` on its sender's wire. Nobody had checked whether it can be drawn inside another member's turn.

**Blocked by:** 07.

**Status:** done

- [x] A steer the agent refuses as it arrives leaves the running reply streaming and the stream open, with nothing written into it. When that reply ends, the steer's message is drawn there with its refusal, and the queue goes on (hub seam; red first).
- [x] A steer's refusal split by the running turn's end (its `Error`, the running turn ends, its `StreamComplete`) ends the steer's turn once and dispatches exactly one queued message (hub seam; mutation-checked).
- [x] The running turn itself failing with a steer waiting still splits there, and the steer runs (hub seam; pins).
- [x] A stop with a refused steer held returns the steer to its sender with the rest, and its refusal is never drawn (hub seam; pins).
- [x] A fire attached mid-turn that ends first starts no steer. The steer is written once the person's turn ends (hub seam; red first).
- [x] The Stop E2E's absence is bought with `Eventually.Settle()`, not a 10 s sleep.
- [x] A reply stopped between its tool calls reloads as a reply naming its tools, neither interrupted nor offered Continue (store level, the real stop path over Redis; green first run, mutation-checked).
- [x] A queued send's refusal is drawn under the refused message, never between another member's message and the reply to it (client seam; the race case red first).

**Notes from the implementation:**

- **No protocol change was needed.**
  - `ChannelMessageNotification.TurnKey` already existed and `McpChannelConnection` maps it.
  - The conversation group answers every reply of a turn under `message.TurnKey ?? Mint()`, its refusals included. `ChatMonitorAssertionTests.ADisabledMembersTurn_IsRefusedReportedAndAnswered` already pins the disabled refusal under the message's key.
  - `send_reply` already carried `turnKey` and `agentInitiated`; the hub just never set or read them.
  - The hub now sends a steer under its queued id as its turn key (`PersonTurns.SteerAsync`). That id is a hub-minted GUID in the same spelling as `TurnKey.Mint()`, so no new state was needed.
- **(a) Held, not written.**
  - `StreamService.HeldForSteer` keeps a reply under a still-waiting steer's key on the steer (`WaitingSteer.Held`). It is checked under the stream lock that `Finish` takes the steer in, so the steer's own replies are either held or, once it has started, written; never both.
  - The running turn's end (`StartSteers`) writes the steer's message, then what was held. If the held replies included the steer's `IsComplete`, the steer's turn is over too: its pending place is given up there and the next steer or queue head starts.
  - The refusal reaches its sender as a refused turn does: drawn under their message, after the reply it was meant to cut, and gone on reload because the agent stored nothing. The alternative, back to the composer, was not taken. A dispatch the hub refuses (signed out) is already drawn this way.
  - Any reply under a steer's key that reached the hub before the running turn's end would now be held too, instead of written mid-reply. That ordering was not observed; it is the same rule.
- **(b) A fire's end starts no steer.**
  - `create_conversation`'s attach carries no key, so `send_reply`'s `agentInitiated` is the only thing that tells a fire's end apart. Such an end gives up its own pending place and never starts a steer (`CompleteStream(…, startsSteers: false)`).
  - A steer is only accepted on a person's turn, which is never agent-initiated.
  - Left as the spec's known edge: a fire already waiting in the agent's pending turns, which runs after the person's turn and before the steer. The hub writes the steer at the person's turn end, and the fire's reply streams under it.
- **The E2E's absence.**
  - A regression that started a queued turn after the stop would have the hub write its message on the stream at dispatch, drawn at once, with Stop shown on both pages. So the settle after both pages go idle is the window that matters.
  - The fresh load after it still checks the stored line.
  - Run alone after the hub and client changes, the images rebuilt by the fixture's staleness check (192 s build phase). `SteerNowE2ETests` was run as well, since every steer now goes out under a turn key, and it passes on the real model.
- **`Transcript.Shown` stopped between calls.**
  - The test drives the real `RedisChatMessageStore` stop path over the real `RedisThreadStateStore`: a call, its result, then a second model call stopped after its reasoning. The second call is dropped, and the line ends on the first call's result.
  - It passed on its first run, pinning existing behaviour.
  - With the `at == line.Count - 1` branch of `EndsAReplyWithoutWords` removed, it fails while ticket 07's test, where a person's message follows, still passes. It pins the line-end case, which nothing did before.
- **A queued send's refusal on the client.**
  - When the send's own wire is read, the refusal is the first chunk and lands under the refused message, before the other member's message and reply that follow on that wire. Fine as it was, pinned.
  - When a resume already held the topic (the send's wire lost, `ShowRefusalsOfAsync`), the error was appended at the end. It was drawn between the other member's message and the reply streaming to it, reading as that turn's answer.
  - Now a refusal off the losing wire is drawn under the send's own message (`AddMessageUnder`, by correlation id; the end, as before, when the page shows none). The other member's bubble was never touched in either case.
  - The race test first flaked under two concurrent processes: it waited for the refusal's toast, which is dispatched just before the message, and let the reply finish meanwhile. The client's `Store.Dispatch` is an unlocked read-modify-write, safe on the browser's one thread but able to lose an update between two test threads. The test now waits for the refusal to be drawn; three concurrent rounds were green after that.

- A refusal drawn under its send is not the topic's last message once the other member's reply follows, so no Retry is offered on it (`MessageList` shows Retry only on the last). The same was true before, once that reply finalised.

**Still open:**
- `HeldForSteer` now runs on every `send_reply` chunk the hub writes: it takes the stream lock and scans the topic's waiting steers (usually none) whenever the reply carries a key, which the agent's always do. Not measured; `send_reply` is the hot path `channels.md` already treats with care.
- In that race the refused message itself is drawn before the other member's message, since the page drew it as sent before the stream was known. Its words do not go back to the composer, as they do for a queued send refused by `EnqueueMessage`. Ticket 08 noted the same for a stop.
- A fire attaching mid-turn still clears `RunningFor` without a push (ticket 07's open edge), and the steered person's Steer now is refused `NotYourTurn` until the next queue change.
