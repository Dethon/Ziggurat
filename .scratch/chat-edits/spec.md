# Chat edits: reroll, versions, delete, edit, fork, continue

Status: ready-for-agent
Branch: `chat-edits`, from `web-identity`

## What a person can do

Each bubble in WebChat gets an actions button. It is a tap target, not hover-only, because the app is used on a phone. It is hidden while the topic streams. It opens a menu with the actions that bubble allows:

| Action | On | What happens |
|---|---|---|
| Reroll | the last reply | The agent answers the same message again. The new reply is a new **version** of that reply. |
| Reroll with… | the last reply | As Reroll, on a model picked from the agent's patchable models. |
| Continue | the last reply, when it was cut off | The agent carries on writing where the reply stopped. The new text joins the same bubble. |
| Edit | any of my messages | Opens the text for editing, with two buttons. **Save** edits in place. **Save & re-run** drops everything after it and the agent answers the new text. Either way the edit is a new version. |
| Edit | any reply | Edits the reply's text in place. The edit is a new version, and no turn runs. |
| Delete from here | any bubble | Removes this message and everything after it on the line being shown. On a reply, this means the whole reply, including its hidden tool calls. |
| Fork from here | any bubble | Opens a new topic that holds the conversation up to this point. On a reply, the copy runs through the end of that reply. On my message, the copy stops just before it and the message's text is put in the new topic's composer. |

The existing copy button stays. Retry on a failed turn is unchanged: a failed turn is still replaced, never versioned.

### Versions (decided: keep alternatives)

- **Every rewrite keeps the old line.** Reroll, edit-and-re-run and edit-in-place each keep what they replace as a version. Delete is the only rewrite that discards anything.
- **The switcher.**
  - A **slot** is the place where versions branch.
  - The first bubble of a slot that holds more than one version shows `‹ 2/3 ›` in its footer.
  - Reroll versions branch after my message, so their switcher sits on the reply.
  - Edit versions branch before my message, so their switcher sits on my message.
  - Versions are ordered by when they were made, and a new version is shown at once.
  - A reply version shows the model that wrote it when that model is not the agent's own.
- **What follows a switch.** Switching makes that version, and the line that was last shown under it, the conversation. Both the model and every page read it from then on.
- **Delete inside a slot.** "Delete from here" on a bubble whose slot holds other versions removes that version and everything under it. The newest remaining version is then shown. When the slot holds no other versions, the conversation simply ends before that bubble (user's decision, 2026-11-15).
- **An in-place edit copies the line below it.** The copy takes new entry ids, so versions of slots further down stay with the old line. They come back when the old version is switched to.
- **A stopped or failed reroll** leaves my message with no reply on the active line. The page draws a "No reply" line with the switcher, so the earlier replies stay reachable.

### Who may do what (decided)

- **Rewriting** (reroll, continue, edit, delete, switch version) is open to anyone who may continue the topic (`TopicViewer.MayContinue`), on any message, including another person's message in a shared topic.
- **Fork** is open to anyone who sees the topic, including a sees-all viewer reading someone else's topic. The fork is the forker's, and it is private.
- **Mirrored topics are fork-only.** These are review copies of Telegram and Sugary conversations, and voice conversations. Such a topic shares its history with another channel, which goes on showing what that channel showed, so it cannot be rewritten.
- **Nothing is rewritten while the topic streams.** The hub refuses the rewrite and the client hides the actions.
- **A turn a service started can't be re-run.** That means a schedule's fire, a login wake, a watch or a download: a user message with an `OriginKind`, or a `ServiceNotice`. Re-sending its prompt as the person's turn would change who it acts for and lose its origin, so reroll and edit-and-re-run are not offered on it. Delete, edit in place and fork still are (user's decision, 2026-11-15).

### Side effects (decided)

- **Warning before a turn runs again.** A reroll, or an edit-and-re-run, whose replaced tail called tools asks for confirmation first. The confirmation names the tools: "This reply used timer_set, web_search. Running it again may do them again." The agent is not told about the earlier run.
- **Memories stay.** Memories already extracted from a deleted or replaced message are not removed. An extraction still pending for a window that has since been rewritten is dropped, never run over different messages.

## How it is stored

**The active line stays where it is.** The agent's history list (`agent-key:{agentId}:{chatId}:{threadId}`) remains exactly what the model reads and what `GetHistory` projects. Nothing on the turn path learns about versions except what a rerun needs (below).

### Entry ids

- **Every persisted message carries an entry id.** It is a GUID in its additional properties (`EntryId`), stamped by `RedisChatMessageStore` when it stores a turn. That covers the failed-turn and stopped-turn paths, and every appender.
- **It is not `ChatMessage.MessageId`.** That id is the provider's response id. Approvals and the client's stream grouping key on it, and it stays as it is.
- **Messages persisted before entry ids** have none. Those are addressed by a derived id, `legacy:{index}:{hash}`, where the hash covers role, timestamp and text. The rewrite recomputes the hash and refuses on a mismatch. The first rewrite of a list stamps real ids on all of it.
- **History carries the ids.** `ChatHistoryMessage` gains `EntryId`.
  - A merged assistant bubble is addressed by its turn, never by one hidden entry. The server resolves "this reply" to every entry between the user message before it and the next user message.
  - A page learns the ids of a turn it streamed by reading the history again when the stream completes.

### Versions

- **Key.** Versions live beside the list, under `agent-versions:{agentId}:{chatId}:{threadId}`. This is a hash with one field per slot, named by the entry id of the message the slot follows, or `root`.
- **Value.** Each field holds the slot's **inactive** versions. Each version is the messages of the line that was active when it was put aside, with their entry ids and their creation time.
- **Switching.**
  1. The active tail after the slot goes into the field.
  2. The chosen version comes out of the field and becomes the tail.
  - Versions of slots inside a put-aside tail stay in the hash under their own entry ids. They come back when that tail does, so nesting costs nothing.
- **Delete** removes the cut tail. It also removes every field named by an entry inside it, and the version itself from its slot.
- **Lifetime.** The key follows the list: the same purge horizon, refreshed together, removed by `DeleteAsync(AgentKey)`, by `/clear` and by `DeleteTopic`. Anything that enumerates or migrates `agent-key:*` must account for it, the household migration included. Check before shipping.

### One rewrite primitive

There is one rewrite primitive, beside `DropTailAsync`. Given an anchor, it replaces the list's tail and moves versions in and out, in one transaction. A guard makes it refuse when the anchor's entry id no longer sits where the page saw it.

**Topic record.** In the same change it keeps the topic record true:
- `MessageCount`, recounted from `Readable`
- every `ReadBy` and `ReadPosition`, clamped
- the snippet, recomputed from the new last readable message
- the TTL

**Search document.** It is rebuilt from the active line, not appended to, so a deleted message stops being searchable. `SetMessagesAsync` is not reused as it stands.

**Attachment files.** A file that no message on the line or in any version still references is deleted with the rewrite.

**Guards.** The rewrite runs as one Lua script and checks two things there:
- **The page's view.** The client sends the anchor's entry id and the entry id of the last message it shows. If either has moved, the rewrite is refused with "This conversation changed; reload it."
- **A turn in flight.** It refuses while `agent-turn:{agentId}:{chatId}:{threadId}` exists, answering "A reply is still being written."
  - The conversation group sets that key before a turn reads its history, and deletes it after the turn is persisted, including a stopped turn's partial reply (ticket 10).
  - It has a TTL as a backstop against a crashed agent, refreshed while the turn runs.
  - This covers what the hub's stream state cannot see: a fire's turn with no live page, and the gap between Stop and the agent persisting the partial reply.

**History changed.** After a rewrite the hub tells the topic's audience `OnHistoryChanged(topicId)`. Every page on it, other than the one that asked, reads the history again before anything else is drawn on it. A rewrite followed by a turn sends the notification before the turn's message is broadcast, so other pages never draw the resent message twice.

### Turns that rewrite

- **Edit and re-run.** The hub puts the edited message and its tail aside as a version of the slot before it, then sends the new text as an ordinary turn. No agent change is needed.
- **Reroll.**
  1. The hub puts the reply aside as a version of the slot after my message. The list now ends at my message.
  2. The hub sends a turn marked as a rerun of that entry (`ChannelMessageNotification.RerunOf`, through to `ChannelMessage`). The turn carries my message's text and attachments, and the reroll's config patch.
  3. `RedisChatMessageStore` leaves that trailing entry out of the history it provides, because the request carries it.
  4. When storing, it does not append the request message again, so my message keeps its id and the version stays on the reply.
  5. A failed rerun marks the stored message failed in place. A retry of it is then an ordinary retry.
  - **The entry is no longer trailing.** Something was appended between the put-aside and the turn, so the rerun is stored as an ordinary turn and its message is appended again under a new id. The put-aside reply stays a version at its slot.
- **A retry keeps the entry id of the message it replaces.** Whenever `FailedTail` drops a failed tail and appends its retry, the retry's user message takes the dropped message's entry id. Versions keyed by that message, such as those of a failed reroll, stay reachable.
- **The model is recorded on the reply.** Every stored reply message records the model that wrote it (`Model`, the turn's resolved model). History projects it, so a reroll on another model can be told apart.
- **Continue** is a turn whose user message is marked `Continuation`.
  - It carries a fixed instruction, spelled once: carry on exactly where the last reply stopped, without repeating it.
  - The model reads it, but `Readable` never projects it, so the new reply draws merged into the previous bubble through `MergeConsecutiveAssistantMessages`.
  - Memory extraction skips it the way it skips a failed turn.

### Cut-off replies

"Continue" is offered on the last reply when it was cut off. Two cases count:

- **Stopped.** A turn the person stopped kept nothing until now. From now on it keeps:
  - the user message;
  - the response messages that completed, with every tool call paired with its result, and any unpaired call dropped;
  - the partial text streamed so far.

  This applies on every channel (user's decision, 2026-11-15), so a Telegram `/cancel` or a voice stop keeps the partial reply too. The partial text is marked `Interrupted`. The model reads it from then on: it was said. History projects it with `IsInterrupted`, and the page draws "Stopped" under it.
- **Truncated.** A reply whose last model call ended because the output token limit was reached is marked `Interrupted` too. The finish reason is read where the stream's updates are collected.

### Mirrored topics

A topic records what it mirrors: `TopicMetadata.Mirrors`, the channel id, set by `ConversationFactory` when the asking channel is not the chat client (a review copy or voice).

For a topic saved before this field, it is derived:
- a topic whose conversation id is not `ConversationIdGenerator.CreateFor(topicId)` is a review copy;
- a topic whose first message's persisted context names the voice channel is voice.

The hub refuses any rewrite of a mirrored topic with "This conversation mirrors <channel>; fork it to change it."

### Fork

- **Copy.** The hub copies the active line up to the cut into a new topic. The topic is created by `SaveTopic(isNew)`'s rules: the forker's, private, in the same space, on the same agent, named `<name> (fork)`.
- **Ids.** Messages get new entry ids. No versions are copied.
- **Attachments.** Files are copied into the fork's conversation directory, and their references are repointed. The fork then keeps its pictures when the source is deleted, and its downloads are checked against the fork.
- **Sandbox paths.** Check whether a message's `SandboxPaths` refers to anything per conversation. If it does, the fork either copies it or leaves it out, and the ticket says which.
- **Topic record.** The copy is stamped like an append (count, snippet, search).
- **Read state.** The fork opens read for the forker.

## Out of scope

- Versions in Telegram and voice.
- Editing a mirrored topic in place.
- Retracting memories.
- Exporting a conversation.
- A tree view of all versions.

## Tests

- **Store.** Each primitive is tested against real Redis: the guard, the topic record's consistency, the search rebuild, and nesting across switches.
- **Reroll's tail shapes.** One test for each:
  - a completed turn;
  - a turn with tool calls;
  - a failed turn, which keeps the existing retry;
  - a stopped turn;
  - a reroll that is itself stopped.
- **Hub.** Each operation's permission and streaming refusals, the mirrored refusal, and the `OnHistoryChanged` ordering.
- **Client.** Reducer and component tests for the menu, the switcher and the edit box. One WebChat E2E test drives reroll, switch, edit-and-re-run, delete and fork through the real stack.
