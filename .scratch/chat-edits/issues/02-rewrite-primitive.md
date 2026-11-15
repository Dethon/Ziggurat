# 02: One rewrite primitive keeps the list, the versions and the topic in step

Status: ready-for-agent
Blocked by: 01
Spec: ../spec.md (Versions, One rewrite primitive)

## What
- **Store operations.** These go on `IThreadStateStore` (`RedisThreadStateStore`), each one transaction, with a guard that refuses when the anchor entry is not where the caller saw it:
  - put the tail after an anchor aside as a version of that slot;
  - switch a slot to one of its versions;
  - cut the tail from an anchor, removing it, the versions nested inside it, and the version from its slot. If the slot has other versions, the newest becomes active.
- **The versions key.** `agent-versions:{agentId}:{chatId}:{threadId}`, following the list's TTL. It is removed by `DeleteAsync(AgentKey)` and by `/clear` (`ChatThreadResolver.ClearAsync`).
  - Check the household migration and anything that scans `agent-key:*` (attachment liveness, retention) and handle the new key there.
- **Every rewrite keeps the topic record true:**
  - `MessageCount` is recounted from `Readable`;
  - `ReadBy` and `ReadPosition` are clamped;
  - the snippet is recomputed;
  - the TTL is refreshed;
  - the search document is rebuilt from the active line.
- **Legacy lists.** A rewrite of a list with no entry ids stamps them first.
- **The read side.** A store read gives the versions per slot, so later tickets can project them.

## Acceptance
These run against real Redis (testcontainers):
- each operation, including the guard's refusal;
- nesting: put aside, switch inside the new line, switch back. The nested versions return.
- a deleted message's words no longer match a search;
- `MessageCount`, `ReadBy` and the snippet match the new line;
- deleting the topic or clearing the thread removes the versions key.
