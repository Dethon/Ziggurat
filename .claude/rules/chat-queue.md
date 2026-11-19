---
paths:
  - "McpChannelSignalR/Services/**"
  - "McpChannelSignalR/Hubs/**"
  - "Domain/Monitor/ConversationGroup.cs"
  - "Domain/Agents/TurnSteering*"
  - "Infrastructure/Agents/McpAgent.cs"
  - "Infrastructure/Agents/ChatClients/**"
  - "WebChat.Client/State/Queue/**"
  - "WebChat.Client/State/Composer/**"
---

# Chat queue, stop and steering

See also: `.claude/rules/channels.md`, `.claude/rules/chat-hub.md`, `.claude/rules/telegram.md`, `.claude/rules/channel-attachments.md`.

Three sides share these contracts: the hub (`StreamService`, `TopicQueue`, `QueueDispatcher`), the page
(WebChat.Client) and the agent (`ConversationGroup`, `McpAgent`). The queue is the hub's, per topic, in
memory, under the stream's lock; a hub restart loses it. A message is queued only while the topic
streams, and the stream ends only once nothing is pending or queued (`GiveUpPlace`; `ChatHub`'s wire
loop ends on the channel completing, not on an `IsComplete`).

## Hub: queue, dispatch, withdrawal

- **A turn ends once, on the reply that says `IsComplete`**, never on its kind. The agent ends a refused
  or failed turn with an `Error` (`IsComplete` false) then a `StreamComplete` under the same turn key;
  the hub's own errors (`PersonTurns` refusals, `AnswerRefusedAsync`, the dispatcher's failure) are a
  turn's last word and carry `IsComplete` true.
- **Admission.** `EnqueueMessage` keeps its signature (installed pages bind it). It holds a place on the
  running stream (`StreamService.Hold`; false means nothing runs and the page opens a stream itself),
  then admits the message as any send: files kept for another conversation refuse it
  (`TopicRefusals.NotYourFiles`, `ChatHub.AdmissionRefusalAsync`, for a turn-opening send too, since the
  agent fetches whatever id a page names; an id the store holds nothing under passes), then the
  capability guard, then the accounts service's word. Refused, it gives the place up and throws a
  `HubException` — never ends a turn on the stream, which would end the running one. A `SendMessage`
  finding a stream running (`StreamService.OpenOrHold`) is queued the same way, so a page cannot jump
  the queue; its refusal is an error chunk on its own wire, not a `HubException`.
- **Dispatch** is `QueueDispatcher`, which depends on `StreamService` and never the reverse
  (`StreamService.Dispatches`). It vouches for the head's session afresh, then starts its turn whether
  or not its sender has a page open; a session signed out meanwhile is written, ended "signed out", and
  the next runs. The stream's current prompt and sender become each head's (and steer's) as it is
  taken: joining pages and the shared-topic approval rule (`ChatHub`, current sender) read them.
- **News** is pushed whole as `OnQueueChanged` (`QueueChangedNotification`) to the topic's audience
  (`QueueNews`, `TopicAudience`, asked afresh each push); the page keeps the later `Revision`, since the
  hub's and the dispatcher's pushes may cross. `GetQueue` answers the same to whoever sees the topic.
  The page catches up on topic open, on becoming live again (`ShownTopicCatchUp`) and on resume
  (`StreamResumeService`), all through the one `QueueChanged` reducer.
- **A queued message is withdrawn by its sender alone, by account** (`RemoveQueued`, `TakeBackQueued` →
  `StreamService.Withdraw`, the same locked step a turn's end takes the head in): either it wins or is
  refused `TopicRefusals.AlreadySent`, never both; anyone else gets `TopicRefusals.NotTheSenders`.
  Remove deletes only this conversation's files (ids were never checked at queueing), none when the
  conversation cannot be read, and only those neither the conversation (`ConversationHistory.HeldAttachments`) nor the
  rest of the queue still names; take-back keeps them. A withdrawal that won is answered whatever its
  best-effort push does (`QueueNews.TellOrLogAsync`). The push item carries no file references; the
  take-back's answer does.

## Stop

- **Stop returns the queue** (`CancelTopic` → `StreamService.StopAsync`): in one locked step every
  queued message, and any head taken but not started, moves to a per-sender **returned** set, and the
  stream is cancelled; only then is the agent told to cancel. Nothing is started after a stop.
- **What tells the agent a turn starts or stops goes out in one order** (`StreamService.InAgentOrderAsync`,
  one semaphore for every topic): a stop's cancel, a queued turn's start and a send's emit
  (`StreamService.StartSendAsync`) never interleave, so a queued turn is either the one cancelled or
  never sent, and a send that opened a new stream reaches the agent after the cancel. A message still
  being admitted or vouched for when the stop lands goes back to its sender too (`Queueing.Returned`),
  and `EnqueueMessage` answers true so the page does not send it behind the cancel; a reroll's message
  and a Continue's ask opening the stream are dropped, not handed back (`SendStart.Dropped`). The hub
  hands back every message it has not drawn and sends nothing after its cancel but a new stream's — the
  agent's `_drawnBefore` rests on this. Other pages hear
  `OnStreamStarted` only once a send has started. A stop returns pending steers first.
- `TopicQueue.LastStopped` (until the topic is deleted) lets a Remove or Edit that lost to a stop be
  refused `TopicRefusals.ReturnedToComposer`.
- The stop pushes `OnQueueChanged` and, to each sender's `account:{userId}` group, `OnQueueReturned`
  (`QueueReturnedNotification.CorrelationIds`, repeated as `ReturnedMessage.CorrelationId`), both
  best-effort, so a stop never fails on a push. `TakeReturned(topic)` first checks the caller still
  sees the topic, then hands over and forgets in one locked step, so only the first page to ask gets them.
- **Unsharing hands members their words** (user's decision, 2026-11-16): `StreamService.Unshare` moves
  every other sender's waiting messages (and unread returned ones) into a per-person set and marks the
  stream `RunningQueue.PrivateTo` the owner (`Queueing.Unshared` for one still being admitted, `EnqueueMessage` answering true). Each
  member is told only after the topic's change, by `OnQueueReturned` marked `Unshared` and takes the words by person
  (`ChatHub.TakeUnshared`, also asked on becoming live) into whatever composer is on screen — the words
  alone; a steer already sent is left to the agent.

## Page

- **Returned messages** are taken while the page shows that topic, on every topic open and on becoming
  live (`ReturnedMessagesEffect`), merged by `ReturnToComposer`; an answer landing after the person moved
  on is kept for that topic's next open, never put in the composer on screen.
- **The composer always sends** (`ChatInputLogic.CanSend` asks nothing of the stream). A send onto a
  running stream (`StreamingService.QueueMessageAsync`) draws nothing and closes no bubble; the page draws it when the hub
  writes it, by its correlation id alone (`IMessagePipeline.QueueUserMessage`) — never skipped as
  "shown" for words the history already holds. A refused or failed send is a toast and its words go
  back (`OfferComposerText`).
- A send the page drew before it heard a reply start, then handed back, is taken off every page of its
  sender (`TakeOffSentMessages`, by the ids `OnQueueReturned` names); refused as queued, its wire says
  so (`ChatStreamMessage.RefusedSend`, optional) and the page gives words and files back through
  `ReturnToComposer`. A refusal drawn under the reader's message keeps its Retry
  (`MessageActionsLogic.OffersRetry`, `RetryRefused`).
- `ReturnToComposer` is the one merge for anything given back (`ComposerState.Handed`). The config patch
  a message was queued with is not kept: words go out on whatever the picker shows, never on a model
  held out of sight (the user's call).

## Steering

- **Steer now** (`ChatHub.SteerQueued(topic, id)`) sends the caller's own queued message into the running turn,
  only while that turn is their own person's (`RunningQueue.RunningFor`, published as
  `QueueChangedNotification.RunningFor`; cleared for a turn `create_conversation` starts, told at once
  through `StreamService.QueueChanges`). Otherwise `TopicRefusals.NotYourTurn`; another's message
  `NotTheSenders`. It is vouched for afresh for the session that queued it (refused: the caller's
  `HubException`, the message keeps its place, the running turn untouched), then in one `InAgentOrderAsync` step moved into the steers (`StreamService.Steer`, counted
  pending) and emitted with `ChannelMessageNotification.Steer` (→ `ChannelMessage.Steer` in
  `McpChannelConnection`; inbound only, `send_reply`'s dictionary untouched) under its queued id as
  `ChannelMessageNotification.TurnKey`, which the agent answers every reply of that turn under
  (`HeldForSteer` relies on it). Unheard → back to the head (`Unsteer`); heard →
  `StreamService.SteerHeard`.
- Until drawn, a steer is published as `QueueChangedNotification.Steering`, beside `Items`, so an older
  page never offers to edit or remove it (`QueueChip.Steering`).
- **Only a steer heard is drawn**: the running turn's end (`StartSteers`) writes the heard steers'
  messages in steer order before their answer; one still being sent is drawn once heard, or returns to
  the head, and if the end drew nothing, nothing runs until then (`RunningQueue.EndedUnheard`) — drawn
  unheard, the agent would wait for a message that never comes.
- **Steers waiting at that end go in together** (user's decision, 2026-11-17): one turn, answered once
  under the first one's key. The hub names them only in its answer to that end's `send_reply`
  (`ReplyReceipt.SteeredTogether`, `StreamService.ReceiptFor`, optional both ways). The agent records
  them (`ConversationGroup.SteeredTogether`) and gathers the rest off its pending turns
  (`SteeredWithAsync`), waiting for one not yet pending with no timer: a steer refused on arrival is
  queued as `AdmittedTurn.Refused`, its refusal drawn under the steers before their answer
  (`m1, m2, error(m2), answer`); a poll answer lost on the wire is redelivered (`.claude/rules/channels.md`,
  "A batch is handed out until its poller says it arrived"), which is why no timer is needed. The turn's model, config patch and format are the last message's.
- **Each steer's key ends once**: the agent ends every key but the first with its own `StreamComplete`
  after the answer (`StreamAgentTurn`'s `joined`); the hub counts those ends without drawing them
  (`RunningQueue.Joined`) and goes on only when all have ended. A stop before the end drew them
  returns them all; a stop after returns none, and the agent keeps the turn as stopped with every message.
  A steer pressed after the steers went in is a steer of their turn, and cuts it.
- **A reply under a waiting steer's key is that steer's, never the running turn's end**
  (`StreamService.HeldForSteer`): a steer refused on arrival gets `Error` + `StreamComplete` while the
  steered turn runs; they are held on the steer and drawn at the running turn's end. Likewise a fire
  attached mid-turn (`send_reply`'s `agentInitiated`) gives up its own place and ends no steered turn.
- **The agent is the authority**: `ConversationGroup` signals the running turn's `TurnSteering` only when
  that turn is a person's (`Origin` null) and its verified account is the steer's. The handle rides
  `TurnRunOptions` → `ChatOptions.AdditionalProperties`, never ambient, so a worker is never cut.
  `ToolApprovalChatClient` sets `FunctionInvocationContext.Terminate` as each call returns; concurrency
  stays on, since the sequential path would leave calls without results. The cut turn is stored whole,
  neither interrupted nor failed (`Transcript.Shown`, `ChatHistoryMessage.ToolsCalled`).

## Stop vs. steer: nothing both returned and kept, nothing drawn lost

- **A stop's cancel names every turn key it handed back** (`ChannelCancelNotification.ReturnedTurnKeys`,
  optional both ways, an empty list rather than none; → `ChannelMessage.ReturnedTurnKeys`). The group
  records them and `TurnSteering.IsReturned` makes `RedisChatMessageStore.KeepStoppedAsync` keep nothing
  for a returned turn, so its words are in the composer alone.
- **The end is answered too**: the agent starts a conversation's next turn only once its last reply's
  `send_reply` returned (`ChatMonitor` awaits each delivery), and the hub answers a reply that says
  `IsComplete` with the last stop's keys (`ReplyReceipt`: `{"returnedTurnKeys":[…],"steeredTogether":[…]}`,
  or plain `ok`, read off the tool's text by `McpChannelConnection`; the agent reads `ok` or anything it
  does not know as nothing). The group records them (`ConversationGroup.TurnsReturned`) and
  drops a pending turn named there. So a steer is either drawn (then cancelled and kept as stopped) or
  returned and never run; a steer already answered (refused on arrival, nothing stored) is still returned.
- **A message the hub drew always survives a stop**: the group keeps a person's message it admitted
  before the cancel that the cancel does not name (`ConversationGroup._drawnBefore`) and the history
  never settled (`TurnSteering.IsSettled`) as a stopped turn with no answer
  (`DisposableAgent.KeepUnansweredAsync`), only while the conversation still exists. A fire, Continue's
  ask, a reroll's message and another channel's message are dropped; a cancel naming nothing (an older
  hub) drops everything.
- **A message sent right behind a stop is answered after the stopped turn is kept**
  (`GroupByStreaming` hands unread messages to a fresh group; `ChatThreadContext.Predecessor`, `Ended`),
  so a chain of stops keeps each turn in page order.
- A steered turn that fails to set up is answered once under the first key, every other key ended
  (`EndUnsetUpAsync`); a retry replaces the failed turn by its last messages (`FailedTail.RetriedBy`, a suffix of
  what its person said, since the page's Retry sends the last message alone).
