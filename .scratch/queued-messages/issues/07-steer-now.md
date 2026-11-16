# 07: Steer now

**What to build:** On the person's own chips, **Steer now** sends a queued message into the running turn.
- **Where it lands.** The agent reads it at the turn's next tool boundary: after the tools it is calling return, and before its next model call. The turn ends there as a complete turn, not marked interrupted and not failed, keeping every tool result.
- **The answer.** The steered message runs next as a person's turn, with everything done so far in view. The page shows the reply split around it.
- **No tool boundary.** If the reply finishes first, the steer simply runs next, ahead of the rest of the queue.
- **Own turn only.** Steer now is offered only while the running turn is the reader's own person's turn. The hub refuses a steer otherwise.
- **Agent's rule.** The agent cuts a turn only for a steer from the account that turn acts for. Any other steer is just the next turn, and a worker's tool loop is never cut by its parent's steer.
- **Wire.** It travels as a `Steer` flag on the channel notification, which only WebChat sets.

**Blocked by:** 02, 03.

**Status:** ready-for-agent

- [ ] A steer arriving during a tool call ends the turn after that call's result. The stored line is the user message, the call, its result, then the steer's turn and its reply (agent seam).
- [ ] A steer during concurrent calls leaves every call's result stored (agent seam).
- [ ] A steer during a text-only reply leaves it whole, and the steer runs next (agent seam).
- [ ] A steer from another account, or during a fire, runs next without cutting (agent seam).
- [ ] A worker's tool loop is not cut by its parent's steer (agent seam).
- [ ] The cut turn is neither interrupted nor failed, and reloads as a reply showing its tool calls even with no text (agent seam).
- [ ] A stop with a steer pending drops it in the teardown, and the hub returns it to its sender (agent and hub seams).
- [ ] Steer is refused for another member's turn and for a fire. A steered item goes ahead of older queued items, and its message lands on the stream after the running turn completes, never mid-reply (hub seam).
- [ ] Steer now is offered only on the reader's own chips while their own turn runs (client seam).
- [ ] E2E (real model): ask for something that takes several tool calls, steer it, and see the reply split around the steer with the second reply answering it.
