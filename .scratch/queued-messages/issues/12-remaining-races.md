# 12: A steered, stopped, raced or unshared message is shown once and never lost

**What to build:** Close the open edges tickets 05 to 09 recorded.

**Blocked by:** 10, 11.

**Status:** ready-for-agent

- [ ] **Stop just after the agent started a steer.** The steer must be either returned or stored, never both: the stop's cancel tells the agent which turn keys it returned, and the agent keeps no stored turn under a returned key (agent + hub seams).
- [ ] **A steer is visible while it waits.** Between Steer now and the running turn's end, the steered message shows as a chip marked as steering, with no Edit or Remove, on every page of the topic, until it is drawn (hub push + client).
- [ ] **A send racing a stop is shown once.** A message the page drew as sent that the hub then returns to the composer is taken off the page (and likewise a refused send in a race with a resume: not drawn as sent, its words back in the composer).
- [ ] **A person's own first send still being vouched for when Stop lands** is not emitted after the cancel: it is returned to its composer like a queued message.
- [ ] **A fire attaching mid-turn** pushes the queue, so no page offers Steer now on a turn that is not the reader's.
- [ ] **Unshare (user's decision, 2026-11-16).** Unsharing pulls a member's queued messages out of the queue; those and any returned words the member had not taken go to that member's open page, into whatever composer is on screen, with a toast saying the topic is no longer shared. A member with no page open gets them on their next page load.
- [ ] **Attachments are the sender's.** A queued or sent message naming file ids the sender does not hold is refused.
- [ ] **A refusal that is not the last message** still offers Retry.
- [ ] **The per-chunk steer check** does not take the stream lock on every chunk when no steer waits.
- [ ] **The steer E2E proves the cut:** it fails with the `Terminate` line removed (mutation-checked once and recorded).
- [ ] Hub, monitor and client filters green under two concurrent processes; the queue E2E collection green.

**Not fixed, by design:** an installed page from before the queue cannot take returned words (its code is old); a queued turn that started just before a stop is the turn the stop cancels; a fire already waiting in the agent's queue runs before a steer (spec's known edge).
