# 01: Every stored message has an entry id

Status: done
Spec: ../spec.md (Entry ids)

## What
- `RedisChatMessageStore` stamps an `EntryId` (a GUID) on every message it persists. This covers ordinary turns and the failed-turn path.
- It goes into the additional properties, read and written through `ChatMessageExtensions`, and never into `ChatMessage.MessageId`.
- `ChatHistoryMessage` gains `EntryId`.
  - A message persisted before this gets the derived id `legacy:{index}:{hash}`, where the hash covers role, timestamp and text.
  - One resolver, which every later ticket uses, turns an entry id back into an index in the list. It recomputes a legacy hash and answers "not found" on a mismatch.
- The client keeps `EntryId` on its message model. When a stream completes, it asks for the history again and fills in the ids of the turn it streamed. No visible flicker: the merge keeps the bubbles it has and adds the ids.

Check first that the turn is persisted before `StreamComplete` reaches the page. If it is not, the re-read finds no ids, and the page waits for `OnHistoryChanged` instead, which the store sends after persisting.

A retry that replaces a failed tail gives its user message the dropped message's entry id (spec: "A retry keeps the entry id…").

## Acceptance
- A persisted turn's messages each carry a distinct entry id, and `GetHistory` returns them.
- A legacy list projects stable derived ids. The resolver finds them, and refuses one whose message changed.
- A test shows that after a streamed turn completes, the page's messages carry the ids `GetHistory` returns.
