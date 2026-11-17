# 13: Messages steered into the same reply go in together

**What to build:** A person queues two messages behind a running reply and presses Steer now on both. Today the first steer cuts the reply at its next tool boundary, but the second waits for the first steer's whole answer, as if it had only been queued: the agent's pump signals only the turn running when a steer arrives, and both arrived while the original turn ran. User's decision (2026-11-17): every message steered before the cut goes in together, in the order they were steered, as **one turn with one answer**.

**Blocked by:** 12.

**Status:** ready-for-agent

- [ ] Two (or more) steers from the running turn's person that are waiting when the cut turn ends start one turn holding all their messages, in steer order, answered once (agent seam).
- [ ] A steer pressed after that merged turn has started cuts it, as a steer cuts any turn of its person's (unchanged; pinned).
- [ ] The hub draws every waiting steer's message where the cut turn ended, in order, before anything of the answer; each steer's chip goes; the merged turn is the running turn, and its end is counted once (hub seam).
- [ ] Stop before the merged turn starts returns every waiting steer to its sender; Stop after it started returns none of them and keeps the turn stored as stopped, never both (hub + agent seams, `ReturnedTurnKeys`).
- [ ] A reload shows the steered messages one after another, then the one answer.
- [ ] The steer E2E (or a new one in `WebChatE2E.Queue`) steers two messages into one reply and sees both drawn before a single answer.
- [ ] Hub, monitor and client filters green under two concurrent processes; queue E2E collection green.
