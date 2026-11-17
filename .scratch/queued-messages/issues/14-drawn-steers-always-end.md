# 14: A steer the hub drew always ends, and is always kept

**What to build:** Close the open edges ticket 13 reported around the moment between the hub drawing steered messages at the cut and the agent starting their turn. One invariant covers three of them: **every key the hub drew ends exactly once, and every message it drew is in the stored line**, whichever way the gathering step is left. User's request (2026-11-17): fix the open items.

The gathering step can be left five ways today, and each must hold the invariant:
1. **Stop while the agent gathers** two or more drawn steers. The hub returned none (they were drawn), and the turn had not started. Today nothing is kept, so the drawn messages are gone on reload. Ticket 13 already decided what happens: a drawn message is not returned, and it is kept as stopped. The reload shows the messages and no answer, as for any stopped turn.
2. **Stop before a lone steer's turn starts.** Same window, same outcome as (1).
3. **The merged turn fails to set up.** Today only the first steer is answered with the error and the others are logged as dropped. The hub keeps their places until a stop, so the topic's queue stalls. Every drawn key must end: the error is answered once, as for a failed turn. Every message is stored, marked failed, so Retry can take it.
4. **A drawn steer never reaches the agent** (`Unsteer` false / `EndUnheardAsync`: its emit was not heard after it was drawn). Today the agent waits for it until the next message, a stop or the group's end. The hub is the side that knows, so the fix goes there. Use the existing handed-back path if it can carry the signal before adding a wire field; any new field is optional both ways. No agent-side timeout, since it would flake under load.
5. **A drawn steer reaches the agent after the stop.** It is stored as stopped and never starts a turn ("nothing may run after the stop", ticket 08).

**Retry after a failed merged turn.** Ticket 13 read this in the code but never ran it: the page's Retry sends the last message alone, and `FailedTail.RetriedBy` compares every message of the failed tail, so the retry is appended after the failed turn instead of replacing it. Reproduce it red first; if it does not reproduce, drop the item and say so in the notes. The expected behaviour follows the reroll precedent: a retry of a failed merged turn replaces it, and its one answer covers every message of it.

**Not in scope:**
- **Mixed agent/hub versions.** That is deploy order (agent before hub), not code.
- **A steer the agent refused after the hub drew it together**, whose error lands in the middle of the answer. Where that error should be drawn is the user's call. Leave it open unless it falls out of this change unchanged in what the page shows.

**Blocked by:** 13.

**Status:** ready-for-agent

- [ ] One theory over the five exits above, at the agent seam (plus the hub seam where the hub decides): each drawn key ends exactly once, and each drawn message is in the stored line, stopped or failed as the exit says. Exits 1 and 2 are red first today.
- [ ] A stop's cancel lets the agent tell "drawn, keep" from "returned, drop" in every interleaving, including a lone steer and a hub from before that sends neither `returnedTurnKeys` nor `steeredTogether`.
- [ ] A merged turn that fails to set up ends every drawn key, so the next queued message is dispatched (hub seam: nothing stalls), and the stored line has every message marked failed.
- [ ] A drawn steer whose emit nobody heard never leaves the agent waiting: the merged turn starts with what did arrive, and the queue goes on (hub + agent seams).
- [ ] Retry on a failed merged turn replaces it, and the answer covers every message (red first, or dropped with a note if it does not reproduce).
- [ ] Reload projections are pinned for the stopped and failed merged turns.
- [ ] Hub, monitor, client and agent filters green under two concurrent processes; queue E2E collection green; full suite green once.
- [ ] `channels.md`'s queue paragraph is updated (still one physical line), and this ticket gets "Notes from the implementation" in ticket 13's format.
