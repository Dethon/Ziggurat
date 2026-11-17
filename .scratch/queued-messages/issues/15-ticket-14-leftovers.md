# 15: What ticket 14 left open

**What to build:** Close the edges ticket 14 left open, so that no message the page has drawn as sent is ever lost to a stop, and no turn waits on a message that will not come. User's request (2026-11-17): fix the open items instead of reporting them.

1. **Every drawn message survives a stop, not only steers.** A stop that lands before its turn starts still drops two kinds of message:
   - a queued message the hub had already dispatched to the agent;
   - a person's own first send that opened the stream.

   Each must be kept the way ticket 14 keeps a drawn steer: stored as a stopped turn with no answer, nothing run after the stop, and the reload showing the message. A message the hub returned to the composer is still dropped, so it is never shown twice. A hub from before keeps the old drop.
2. **A turn never waits on a steer whose agent connection dropped.** Today, when the agent's connection drops after a steer was confirmed delivered, the agent can wait for it until the next message, a stop or the group's end. The waiting turn must start with what it has, or the steer must be delivered again. Either way:
   - nothing waits on a timer (no agent-side timeouts, since they flake under load);
   - each key ends exactly once;
   - no message is answered twice.
3. **A deleted topic is never rewritten.** `KeepUnansweredAsync` must not write history for a conversation that was deleted while a stop was being torn down. It needs the same check the stopped-turn path has. Red first.
4. **A steer refused late is not drawn mid-answer.** A steer the agent refused after the hub drew it together currently has its error written in the middle of the answer. The agent knows about the refusal before the merged turn starts (the gathering step waits for each steer), so the error goes out before the answer begins. That follows the precedent of a steer refused as it arrived (`m1, m2, error(m2), m3, answer`), as closely as the drawn order allows.
5. **The branch ticket 14 only read is run.** The agent picks up an already-received steer after the stop. Pin it with a deterministic test, so both ways a steer can leave (that branch and the end-of-group drain) are run, not read.

**Not in scope:** mixed agent/hub versions. That is handled by deploy order (agent before hub), not code.

**Blocked by:** 14.

**Status:** ready-for-agent

- [ ] Each item is red first at the hub or agent seam (or the client seam where the page decides), using deterministic holds or gates, never sleeps.
- [ ] Reload projections are pinned for item 1's stopped messages.
- [ ] Hub, monitor, client, agent, store, connection and composition filters green under two concurrent processes; queue E2E collection green; full suite green once.
- [ ] `channels.md`'s queue paragraph is updated (still one physical line); "Notes from the implementation" in ticket 13's format, including anything still open, with why it could not be closed here.
