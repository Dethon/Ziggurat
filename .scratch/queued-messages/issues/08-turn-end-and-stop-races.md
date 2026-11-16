# 08: A turn ends once, and a stop loses or runs nothing

**What to build:** Close the races tickets 03 to 06 reported, so a queued message is never sent twice, never sent after a stop, and never lost.

**Turns ending twice.** The agent ends a failed or refused turn with an `Error` and then a `StreamComplete` under the same turn key. Today the hub counts both as turns ending, which has two effects:
- with two messages queued, both are dispatched at once;
- with one message queued, the second "end" can close the stream under it.

A turn must end once, however it ends.

**Stop.** In each of these, the message must come back to its sender's composer, and nothing may run after the stop:
- **Cancel before return.** `CancelTopic` tells the agent to cancel before it returns the queue, so a turn ending in between can dispatch the head straight after the cancel. The queue must be taken back first.
- **Still being admitted.** A message the hub is still admitting when Stop lands (vouching, capability check) is neither queued nor returned, and the page then sends it as a new turn after the cancel.
- **On the stream, not yet emitted.** A message the dispatcher has written on the stream but not yet sent to the agent, when Stop lands, is drawn on the page but neither sent nor returned.

**Withdrawing after a stop.** A Remove or Edit that loses to a Stop answers "already sent", although the message went back to the composer. It must say the message went back to the composer.

**Edit keeps the model.** Edit drops the queued message's config patch, so the message is re-sent on whatever the picker shows now. The composer must keep the returned patch and send with it.

**Blocked by:** 06.

**Status:** ready-for-agent

- [ ] An `Error` followed by `StreamComplete` for one turn dispatches exactly one queued message, and with one message queued the stream stays open until that message's turn ends (hub seam).
- [ ] A turn ending while a stop is in progress dispatches nothing, and the stopped queue is returned in full (hub seam).
- [ ] A message admitted concurrently with a stop is returned to its sender, never emitted (hub seam, run under two concurrent test processes).
- [ ] A dispatch interrupted by a stop between writing and emitting returns the message, and the page shows it in the composer, not as a sent message (hub and client seams).
- [ ] A Remove or Edit that loses to a Stop gets a refusal that says the message went back to the composer (hub seam).
- [ ] A taken-back or returned message is sent again on the model it was queued with (client seam).
- [ ] The queue E2E collection still passes.
