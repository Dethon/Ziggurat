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

**Status:** done

- [x] An `Error` followed by `StreamComplete` for one turn dispatches exactly one queued message, and with one message queued the stream stays open until that message's turn ends (hub seam).
- [x] A turn ending while a stop is in progress dispatches nothing, and the stopped queue is returned in full (hub seam).
- [x] A message admitted concurrently with a stop is returned to its sender, never emitted (hub seam, run under two concurrent test processes).
- [x] A dispatch interrupted by a stop between writing and emitting returns the message, and the page shows it in the composer, not as a sent message (hub and client seams).
- [x] A Remove or Edit that loses to a Stop gets a refusal that says the message went back to the composer (hub seam).
- [x] A taken-back or returned message is sent again on the model it was queued with (client seam).
- [x] The queue E2E collection still passes.

**Notes from the implementation:**
- The turn-end rule: a reply ends its turn when it says `IsComplete`, never by its kind. The agent's two error paths (`ReplyDispatcher`'s `ErrorContent`, `ConversationGroup.AnswerRefusedAsync`) send the `Error` with `IsComplete` false and then `StreamComplete`; the hub's own errors (`PersonTurns.EndTurnAsync` for unvouched, signed out and not live, `ChatHub.AnswerRefusedAsync`, the dispatcher's failure) send `IsComplete` true with nothing after. The error chunk on the wire is unchanged. Given up: an agent `Error` whose `StreamComplete` never arrives no longer ends the stream, the same exposure a text turn already had.
- One semaphore (`StreamService.InAgentOrderAsync`, every topic, in-memory steps only) orders what reaches the agent: the stop's locked step then its cancel, a queued turn's start (claim, write, emit) and `SendTurn`'s emit. The stop now ends the stream before the cancel goes out, so a send in between opens a stream of its own; its emit waits for the cancel.
- The dispatcher vouches before the turn starts, so "written, not emitted" no longer exists; a refused sender's message is still written before the refusal.
- A message admitted as the stop lands is returned (`Queueing.Returned`) and `EnqueueMessage` answers true. The topic's last stopped stream and the ids it returned are kept until the topic is deleted, which is what the Remove/Edit refusal (`TopicRefusals.ReturnedToComposer`) reads, so it holds after a page took them.
- Edit keeps the model: `ComposerState.ReturnedPatches` holds, per topic, the patch of the earliest returned message (wrapped, so "queued on the agent's own" is not "none"); a patch already held wins over a later return. The composer's next send there goes on it, whether it opens a stream or is queued again; it is spent when that send lands, and any pick in the model or effort menu drops it. A retry now sends its own files (none is `[]`, not null), so it neither takes the held patch nor, as it used to, the composer's ready files. Not shown on screen: a person who deletes the returned words sends their next message on that topic on the held model.
- An installed page from before, which never takes returns, now hears `EnqueueMessage` answer true for a message a stop returned as it was admitted, so it clears the words and does not get them back; before, it re-sent them behind the cancel.
- A queued turn that started just before the stop is the turn the stop cancels: its message stays drawn and is not returned.
- The client test that a returned message is never drawn (`QueuedSendTests`) was green on its first run, the hub no longer having a written-not-emitted state: it pins the page's half.
- Still open: a `SendMessage` racing a stream that a stop takes while it is admitted returns the message to the composer, but that page had drawn it as sent, so it shows both; and a person's own send that opened the stream and is still being vouched for when Stop lands is emitted after the cancel with no stream to answer on (unchanged from before).
