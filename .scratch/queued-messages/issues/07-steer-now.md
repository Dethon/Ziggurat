# 07: Steer now

**What to build:** On the person's own chips, **Steer now** sends a queued message into the running turn.
- **Where it lands.** The agent reads it at the turn's next tool boundary: after the tools it is calling return, and before its next model call. The turn ends there as a complete turn, not marked interrupted and not failed, keeping every tool result.
- **The answer.** The steered message runs next as a person's turn, with everything done so far in view. The page shows the reply split around it.
- **No tool boundary.** If the reply finishes first, the steer simply runs next, ahead of the rest of the queue.
- **Own turn only.** Steer now is offered only while the running turn is the reader's own person's turn. The hub refuses a steer otherwise.
- **Agent's rule.** The agent cuts a turn only for a steer from the account that turn acts for. Any other steer is just the next turn, and a worker's tool loop is never cut by its parent's steer.
- **Wire.** It travels as a `Steer` flag on the channel notification, which only WebChat sets.

**Blocked by:** 02, 03.

**Status:** done

- [x] A steer arriving during a tool call ends the turn after that call's result. The stored line is the user message, the call, its result, then the steer's turn and its reply (agent seam).
- [x] A steer during concurrent calls leaves every call's result stored (agent seam).
- [x] A steer during a text-only reply leaves it whole, and the steer runs next (agent seam).
- [x] A steer from another account, or during a fire, runs next without cutting (agent seam).
- [x] A worker's tool loop is not cut by its parent's steer (agent seam).
- [x] The cut turn is neither interrupted nor failed, and reloads as a reply showing its tool calls even with no text (agent seam).
- [x] A stop with a steer pending drops it in the teardown, and the hub returns it to its sender (agent and hub seams).
- [x] Steer is refused for another member's turn and for a fire. A steered item goes ahead of older queued items, and its message lands on the stream after the running turn completes, never mid-reply (hub seam).
- [x] Steer now is offered only on the reader's own chips while their own turn runs (client seam).
- [x] E2E (real model): ask for something that takes several tool calls, steer it, and see the reply split around the steer with the second reply answering it.

**Notes from the implementation:**
- The handle: `TurnSteering` (`Domain/Agents`), made per turn by `ConversationGroup` and kept as the running turn beside its person (`Origin` null, the verified account; none for a fire or the guest). The group hands it to the agent as `TurnRunOptions`; `McpAgent` treats those as the group's (not caller-supplied options that replace its own), builds its options as before and puts the handle in `ChatOptions.AdditionalProperties` (`TurnSteering.OptionsKey`). `ToolApprovalChatClient` reads it off `context.Options` in `InvokeFunctionAsync`'s finally and sets `Terminate`. A worker's run (`SubAgentRunTool`) builds its own options, so it never sees its parent's handle. The pump signals before it queues the steer, so a steer never cuts its own turn.
- Hub: `SteerQueued(topic, id)`; refusals `TopicRefusals.NotYourTurn` (another member's turn, a fire, a queued head claimed but not started) and `NotTheSenders` (now "remove, edit or steer with it"); `AlreadySent`/`ReturnedToComposer` as a withdrawal. Steers wait in `StreamService._steers`, counted as pending; the running turn's end writes the first one's message (`Finish`). Whose own turn runs is `_runningFor`, told as `QueueChangedNotification.RunningFor` (additive), and the dispatcher pushes the queue again once a head starts so its sender's page offers Steer now.
- Vouching: a steer is vouched for by the session that queued the message, not the caller's, as its dispatch would be, so a device signed out meanwhile has what it left behind steered by nobody (`ASteerOfAMessageFromASessionSignedOutSince_…`). A steer nobody hears goes back to the head of the queue with `PersonTurns.NotLiveError` and leaves the running turn alone.
- Reload: a reply that ended on tool results without a word was drawn as nothing. `Transcript.Shown` (now the one rule over a whole line: projection, counts, search text, the rewrite's last-seen) shows its last results message, named with its tools (`ToolsCalled`), and the page shows them as its tool calls. It also draws a reply stopped between calls (kept with its results and no text, so nothing is marked interrupted and no Continue is offered); that case was read from `RedisChatMessageStore`, not tested. The row goes by the id its calls streamed under, so the page names its streamed bubble from it and a rewrite's last-seen matches.
- Green on first run (pins, mutation-checked where marked): concurrent calls keep their results (library behaviour); text-only reply; another account's steer and a delegated fire for the same account (both red with the person check removed); the worker; a stop dropping the steer at the agent; hub refusals (red without the running-turn check); stop returning the steer (red without it); `RunningFor` after a dispatch (red without the second push).
- Open: a stop racing the running turn's end — the agent has ended the turn and started the steer, the hub has not yet heard — returns the steer to the composer while the agent stores it as a stopped turn, so the person has it twice. A fire attached to the topic that completes while the person's turn runs counts as a turn end, writing a waiting steer's message mid-reply. Between Steer now and the running turn's end the message is neither a chip nor drawn on any page. An agent refusing a steer as it arrives (a member disabled meanwhile) sends its `Error`+`StreamComplete` while the steered turn still runs, which the hub counts as that turn's end. A fire attaching mid-turn clears `RunningFor` without a push, so a page may offer Steer now and be refused. The E2E's proof of the cut is the model not saying the word it was told to say after its tool, which depends on the model obeying; it was not run with the cut removed.

