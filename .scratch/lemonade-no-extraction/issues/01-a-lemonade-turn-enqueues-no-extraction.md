# 01 — A Lemonade turn enqueues no extraction

**What to build:** A person chatting through a Lemonade model has nothing from that turn written
to memory. Their message is never handed to the hosted extraction model — the enqueue simply does
not happen. The same turn still receives its recall block, so the local model keeps everything the
agent already remembered about them.

The skip reads the turn's patchable model, which is the only signal available at that point: the
recall hook runs before the agent, so no host has been chosen yet. That means it fails closed — a
turn that *asked for* a Lemonade model is not extracted from even if routing later fell back to a
hosted model. Ticket 02 removes that fallback; this ticket must be correct without it.

The skip belongs beside the existing per-agent `memory` feature gate, as one more reason the
enqueue does not happen, stated where the other one already is. It returns before the memory
anchor is taken, so the anchor's contract is untouched.

Follow Red-Green-Refactor. The recall hook's unit tests already hold a family asserting extraction
*is* enqueued under awkward conditions (embedding failure, unremembered user, unavailable thread
context, anchor equal to persisted count); these cases are that family's negative and should read
like it.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A message whose patchable model is a Lemonade model enqueues nothing for extraction.
- [ ] That same message still gets its recall block attached — asserted in the same test, because
      the one-way boundary is the point and splitting it lets a later change break half of it
      silently.
- [ ] A message patched to a hosted model still enqueues, unchanged.
- [ ] A message with no patchable model at all still enqueues, unchanged.
- [ ] An unremembered user on a Lemonade turn enqueues nothing, and does not become remembered by
      that turn.
- [ ] An agent with the `memory` feature disabled behaves exactly as before; the two gates do not
      interact.
- [ ] A Lemonade model is identified through the existing helper for its namespaced form — no new
      marker, no capability inference, no new configuration key.
- [ ] The memory anchor's existing pinned test stays green and gains no additions.
- [ ] No new setting can turn this off.
