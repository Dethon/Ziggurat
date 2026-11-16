# Queued messages and steering in WebChat

Status: ready-for-agent
Branch: `queued-messages`, from `master`

## Problem Statement

While the agent is answering in the chat client, I can't send anything. The composer lets me type, but its send button has turned into Stop, and Enter does nothing. If I think of a follow-up, a correction or something I forgot, I can either wait for the whole reply or stop it and lose its direction.

What hurts most is when a reply runs for a long time through tools: searching, browsing, running commands. I can watch it head the wrong way, or see something I could have told it, and still have no means of saying so short of killing the turn.

Telegram does not have this problem: a message sent mid-reply simply waits and runs next. The chat client is the one place where I'm made to wait.

## Solution

The composer always sends. A message sent while a reply is running is **queued**: it waits its turn and runs as soon as the running turn ends. Queued messages show above the composer, and I can take one back to change it, remove it, or **steer** with it.

Steering puts my queued message into the running turn at its next tool boundary: once the tools it is calling have returned, and before it asks the model again. The reply ends there, my message follows, and the agent answers it with everything it has done so far in view. A reply that never reaches a tool call is not cut: my message just runs next, ahead of the rest of the queue.

Stop still ends the reply, and every queued message goes back into its sender's composer, so nothing runs that its sender didn't see coming.

The queue is the topic's, held by the server, so my other devices and anyone else reading a shared topic see the same queue. Telegram, voice, Sugary and scheduled tasks behave exactly as they do now.

## User Stories

1. As a person chatting in WebChat, I want to send a message while the agent is still answering, so that I can say what I just thought of without waiting.
2. As a person chatting, I want a message sent mid-reply to run after the current reply rather than interrupt it, so that the answer I'm reading is not lost.
3. As a person chatting, I want to see my queued messages above the composer, so that I know what will run next.
4. As a person chatting, I want queued messages shown in the order they will run, so that I can predict what the agent will see.
5. As a person chatting, I want a queued message to show its files as a count, so that I can tell what it carries without opening it.
6. As a person chatting, I want queued messages to run one after another without my doing anything, so that I can queue several thoughts and walk away.
7. As a person chatting, I want a queued message to appear in the conversation only when its turn starts, so that the transcript reads in the order things actually happened.
8. As a person chatting, I want the reply I'm reading to keep streaming in its own bubble after I queue something, so that my queued message does not cut it in two on screen.
9. As a person chatting, I want to remove a queued message, so that I can change my mind before it runs.
10. As a person chatting, I want the files of a message I removed to be deleted, so that nothing I withdrew lingers in storage.
11. As a person chatting, I want to take a queued message back into the composer with its text and files, so that I can rework it before sending it again.
12. As a person chatting, I want a message I edited and sent again to join the end of the queue, so that it is clear it is a new message.
13. As a person chatting, I want "already sent" when I try to remove or edit a message whose turn has just started, so that I'm never shown a withdrawal that did not happen.
14. As a person chatting, I want to steer the running reply with one of my queued messages, so that I can correct its course without throwing away what it has done.
15. As a person chatting, I want a steered message to reach the agent at its next tool boundary, so that it stops spending time and money going the wrong way.
16. As a person chatting, I want the agent's answer to a steer to take into account the tools it already ran, so that I don't pay for that work twice.
17. As a person chatting, I want the reply to split at the steer, with what came before, then my message, then the answer, so that I can read what changed and why.
18. As a person chatting, I want a steer sent during a reply that calls no tools to run straight after it, ahead of anything else I queued, so that steering is never lost and never waits behind older messages.
19. As a person chatting, I want a steered message to behave like any other message of mine afterwards (edit, re-run, reroll, fork, delete from here), so that I don't have to learn a second kind of message.
20. As a person chatting, I want the part of the reply cut short by a steer to show the tools it ran, even when it said nothing in words, so that I can see what was done before my steer.
21. As a person chatting, I want a reply ended by my steer not to be offered "Continue", so that I'm not invited to resume something I redirected on purpose.
22. As a person chatting, I want Stop to stay reachable while a reply runs even when I'm typing, so that sending and stopping never compete for one button.
23. As a person chatting, I want Stop to put my queued messages back in my composer, so that stopping never runs something I'd queued for a reply I've just abandoned.
24. As a person chatting, I want returned messages placed before anything I was already typing, joined by blank lines, with their files ready to send, so that nothing I wrote is overwritten.
25. As a person chatting, I want my queued message to run even if I close the tab, so that queueing is fire-and-forget.
26. As a person chatting, I want a message queued under a session that was signed out meanwhile to be refused when its turn comes, so that a revoked device can't act through a message it left behind.
27. As a person with the chat open on my phone and my laptop, I want both to show the same queue, so that I can manage it from either.
28. As a person with several devices, I want messages returned by Stop to land on only one of them, so that I don't end up sending them twice.
29. As a person who reloads the page or reconnects mid-reply, I want the queue shown again as it is, so that a reconnect doesn't make my queued messages look lost.
30. As a person with no page open on a topic when someone stops its reply, I want my returned messages waiting in the composer the next time I open the topic, so that they are not lost.
31. As a member of a shared topic, I want to see what others have queued, with their names, so that I know what will run before my message.
32. As a member of a shared topic, I want to queue a message while another member's reply is running, so that I don't have to wait for them to finish.
33. As a member of a shared topic, I want only the sender of a queued message to remove, edit or steer with it, so that nobody withdraws what I asked.
34. As a member of a shared topic, I want "Steer now" offered only on a reply that is my own turn, so that the agent's tools never act as another person on my words.
35. As a member of a shared topic, I want my queued message returned to my own composer when another member stops the reply, so that it is never run or discarded without me seeing it.
36. As someone who sees every topic of the household, I want to see a topic's queue only where I can see the topic, so that private conversations stay private.
37. As a person chatting while a scheduled task's turn is running in my topic, I want my message to queue behind it and not be able to steer it, so that a task acting for its writer is never redirected by a chat message.
38. As a person on an installed app from before this change, I want sending and stopping to keep working, so that an update deployed under me breaks nothing.
39. As a person chatting on Telegram, I want my messages to behave exactly as now, so that this change is invisible there.
40. As a person whose queued message carries a different model or reasoning effort from the picker, I want its turn to run on what I picked when I sent it, so that queueing doesn't change which model answers.
41. As a person queueing a message with a file the agent's model can't read, I want the refusal straight away, as for any send, so that I don't find out minutes later.
42. As the operator, I want a queued turn to pass the same identity and standing checks as any other turn when it is dispatched, so that queueing opens no way around them.

## Implementation Decisions

Decisions with the user, 2026-11-16:
- queue by default, steer on demand;
- the queue is the hub's;
- Edit pulls the message back into the composer, chosen over editing in place;
- Stop returns every queued message to its own sender;
- only the running turn's own person may steer it;
- a steer starts a person's turn;
- steering takes effect at the next tool boundary only;
- WebChat only, behind an explicit flag.

### The queue (chat hub)

- **The stream service keeps the queue state per topic, beside the topic stream it already keeps, and in memory as that stream is.** There are three parts:
  - **queued**: held by the hub, not yet sent to the agent;
  - **dispatched-not-started**: sent to the agent, but its turn has not begun on the stream;
  - **returned**: per sender, items Stop handed back that no page of theirs has taken yet.
- **A queue item carries everything needed to send it without a hub call in hand:**
  - the text, attachments, config patch and correlation id;
  - when it was queued;
  - the sender's account and principal, and the session their connection's assertion named;
  - their display name;
  - the topic's conversation identity, agent and space.
- **A non-empty queue means a live topic stream.** Items are only added while the topic streams, and the stream ends only when nothing is pending, dispatched or queued. Rewrites are already refused while a topic streams, so they never meet a queue.
- **Hub calls:**
  - `EnqueueMessage` keeps its signature, since installed pages bind it, and its admission checks (the capability refusal and vouching). It appends to the queue instead of writing to the stream and emitting.
  - `GetQueue(topic)`
  - `RemoveQueued(topic, item)`
  - `TakeBackQueued(topic, item)`, which returns the item
  - `SteerQueued(topic, item)`
  - `TakeReturned(topic)`
  - `CancelTopic` cancels as today, then returns the queue (see Stop).
  - Every call that names a topic resolves it under the topic visibility rule first, as every hub call does. Remove, take back and steer are the sender's alone, by account. Once the item has left the queue they are refused with "already sent".
- **A send that finds a stream joins the queue.** A `SendMessage` arriving on a topic that already streams is queued instead of emitted, so a page that had not yet heard the stream start cannot jump the queue. The caller joins the stream as now.
- **Hub pushes:**
  - `OnQueueChanged(topic, items)` goes to the topic's audience by the same rule as its stream: a private topic's owner and the sees-all group.
  - `OnQueueReturned(topic)` goes to a sender's own connections through the per-account group they already join.
  - Items carry the sender's name and account, so a page labels other people's items and offers actions on its own.

### Dispatch

- **Dispatch runs outside any hub call.** A turn completes when the agent's `send_reply` finishes it in the stream service, where there is no SignalR connection context. So dispatch is a collaborator of the stream service, not a `ChatHub` method.
- **When a turn completes and nothing dispatched is waiting to start, dispatch takes the head of the queue.** It writes the head's user message on the stream, vouches it afresh, and emits it.
  - Vouching asks the session accounts service by the item's session id, as the hub does for a connection.
  - A refusal (a session signed out meanwhile, the accounts service unreachable) or an emit that nobody receives ends that item's turn on the stream as it would end today. The next item is then dispatched.
- **A queued message runs whether or not its sender still has a page open.**
- **Vouching runs twice: at queueing and again at dispatch.** At queueing, an obvious refusal shows at once. At dispatch, a session revoked while the message waited stops it, as the per-turn refresh stops a live connection's next turn. The agent's own admission (assertion, standing, suspension) is unchanged and runs as the message arrives there.

### Stop

- **Stop empties the queue.** `CancelTopic` cancels the turn and the stream as today. It then moves every queued item, and any dispatched-not-started steer, into returned under its sender, and pushes `OnQueueChanged` and `OnQueueReturned`.
- **Taking returned items is atomic per person.** A page takes them with `TakeReturned` when told and showing that topic, and on every topic open. The first device to take them gets them.
- **The agent drops a steer it held as pending in the cancel's teardown**, as it drops any pending turn today. The hub has already returned that steer to its sender.
- **Why nothing restarts after a stop.** Keeping other people's messages queued would mean starting a new stream right after the cancel, and that races the agent's teardown, which drops anything that arrives behind a cancel. Returning them avoids the race.

### Steering (agent)

- **The channel notification gains `Steer`.** It is mapped onto the channel message through the channel protocol, so it is spelled once. Nothing outbound changes. Only WebChat sets it.
- **A steer is a turn and is admitted like one.** The conversation group checks its assertion, standing and suspension as it arrives, then queues it in the group's pending turns like any other, so it is the next to run.
  - When the running turn is a person's turn whose verified account is the steer's account, the group also signals that turn's **steering handle**.
  - A steer that matches no running turn is simply the next turn, whether there is no turn running, the turn is another person's, or it is a fire.
- **The steering handle is created per turn by the group and passed down explicitly on the turn's run options.** No ambient state. A worker builds its own options, so a parent's steer never cuts a worker's loop.
- **The tool loop's approval client checks the handle after each tool call returns.** When the handle is signalled, it asks the function-invoking loop to terminate, the same hook a rejected approval already uses. The loop then stops before its next model call.
  - The client runs a batch of calls concurrently, so there is no "last call by index" rule.
  - The invariant is that every call the model made has its result in the stored line. A call without one makes the next request a provider 400.
  - How the library's loop treats termination, in its concurrent and its sequential paths, is to be read from the library before relying on it.
- **A turn cut by a steer is a complete turn.** It is stored like any other and ends with tool results, possibly with no text. It is not marked interrupted, and it completes on the stream. The steer's turn then runs from the pending turns.
- **A steer starts a person's turn in the stored line**, because it is one. The turn-boundary rule, rewrites, forks, Continue, failed-turn replacement and memory extraction are unchanged.
- **No fallback code.** If the turn ends before any tool boundary, the handle is never read, and the steer runs next because it is already first in the pending turns.

### Steer now (hub)

- **`SteerQueued` checks two things before steering.** The caller must have sent the item, and the topic stream's current turn must be a person's turn for the caller's account. The hub knows the stream's current sender.
- **A steer is emitted at once with `Steer` set**, after vouching afresh. It goes to the front of dispatched-not-started, and nothing else is dispatched until the steer's turn has started.
- **The steer's user message is written on the stream when the running turn completes.** That is where the split falls, whether or not the agent cut the turn short.
- **The agent is the authority.** If it finds the steer matches no running turn, the message is still the next turn, and the page draws it the same way.

### The page

- **The composer sends while a reply streams.** The send rule no longer refuses during a stream. The control shows Send whenever there is something to send, with Stop beside it while the topic streams. Enter queues.
- **A queued send is not drawn when it is sent.** Today the page draws a person's message locally and closes the reply bubble before sending. It then skips its own message on the stream by correlation id.
  - A send onto a running stream does neither. The message shows only as a chip until it is dispatched.
  - When the hub writes it on the stream, the sending page draws it rather than skipping it.
  - A send that opens a new stream keeps today's behaviour.
- **Queue state on the page** is a client store fed by `OnQueueChanged`. It is caught up by `GetQueue` on topic open, resume and reconnect, as part of catch-up.
- **Chips** sit above the composer.
  - Each shows an excerpt, a file count, and the sender's name when it is not the reader.
  - The reader's own chips offer Edit, Remove and Steer now. Steer now appears only while the running turn is the reader's own.
  - Icons are drawn as inline SVG glyphs, as the client UI rule requires.
- **Returned and taken-back items are merged into the composer.** Their text goes before whatever the composer holds, joined by blank lines, and their attachments are re-added as ready references.

## Testing Decisions

- **A good test drives the feature from the edge a person or a channel touches, and asserts what they would observe**: what the stream carried and in what order, what the agent was sent, what the stored line holds, what the composer and the chips show. It never asserts how the queue is held internally. Tests must not fail under load: a flake under two concurrent test processes is a defect to fix.
- **Hub seam (existing).** The hub and stream service in-process over a channel inbox, as the chat hub's not-live, rerun and stream tests build them. The test plays the agent: it reads the inbox and finishes turns through the stream service, as `send_reply` does. It covers:
  - queue order and dispatch at turn end;
  - the stream staying open across hand-offs;
  - vouching at dispatch, and a signed-out session refused there;
  - a send that races a stream being queued;
  - remove, take back and steer permissions, and "already sent" races;
  - Stop returning items to each sender, with take-once across devices;
  - private-topic audiences.
- **Agent seam (new, at the monitor).** The chat monitor driven through the fake channel connection, as the monitor tests drive it. Its agent is built over the real tool-loop approval client wrapping a scripted model and a local tool, instead of a fake agent. Prior art:
  - the monitor tests, for driving and observing a group;
  - the approval client's written-order tests, for a scripted model under the real loop.

  It covers:
  - a steer cutting after a call's result;
  - concurrent calls all keeping their results;
  - a text-only reply left whole, with the steer running next;
  - another account's steer, or one during a fire, not cutting;
  - a worker's loop not cut;
  - the cut turn neither interrupted nor failed;
  - a stop dropping a pending steer.
- **Client seam (existing).** The whole-client scripted fixture: real stores, effects and services over a scripted hub. It covers:
  - the composer sending while streaming;
  - a queued send not drawn, and drawn once when dispatched;
  - the chips;
  - Steer now offered only on the reader's own turn;
  - the take-back and returned-items merge.
- **One end-to-end test** in the WebChat Playwright suite, on the real model as the E2E suite always runs. It covers:
  - queueing two messages behind a tool-using reply and seeing them run in order;
  - a second page seeing the same queue;
  - steering a multi-step reply and seeing it split around the steer;
  - Stop returning queued text to the composer.

## Out of Scope

- Steering from Telegram, voice or any other channel. The agent's handling is channel-agnostic, so a channel can opt in later by setting the flag.
- Cutting a model call while it streams, which would make steering work on text-only replies.
- Reordering the queue.
- Editing a queued message in place.
- A queue, or returned items, that survive a restart of the chat hub.
- Telling the model that a steer interrupted it. The steer arrives as the next person's turn, and the turn before it shows where it ended.

## Further Notes

- **Queueing already worked end to end before this.** The hub had `EnqueueMessage`, the client called it whenever a topic streamed, and the conversation group already runs turns one after another. The only block was the composer refusing to send while streaming, a choice of how the composer behaves rather than a bug fix. This feature moves the queue up from the agent's pending turns into the hub, where it can be shown, withdrawn and returned.
- **Known edge.** The hub sees turns complete, not start. If a fire or another channel's turn is already waiting in the agent's pending turns for the same conversation, the hub draws the queued or steered message after the running turn, and the fire's reply then streams under it. The stored line is right, and a reload draws it in order.
- **Vocabulary.** The glossary's **Chat command** is described as a message that "steers the conversation instead of being answered by it". A steer here is the opposite: a turn, answered like any other. If the term needs pinning, the glossary should gain **Queued message** (a person's message the hub holds until the running turn ends) and **Steer** (a queued message sent into the running turn at its next tool boundary, which still starts a person's turn), and Chat command's wording should drop "steers".
- **Implementation tickets** are in `issues/`, as vertical slices:
  - 01 and 02 are prefactors that can start at once;
  - 03 is the tracer bullet;
  - 04 and 05 follow 03;
  - 06 follows 05;
  - 07 needs 02 and 03.
