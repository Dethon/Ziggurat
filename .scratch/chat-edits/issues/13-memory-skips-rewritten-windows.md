# 13: A pending extraction over a rewritten window is dropped

Status: ready-for-agent
Blocked by: 02
Spec: ../spec.md (Side effects)

## What
- Read `.claude/rules/memory-architecture.md` first.
- **The anchor records an entry id.** `MemoryAnchor` records the entry id of the last persisted message it covers, beside `PersistedMessageCount`.
- **The worker checks it.** `MemoryExtractionWorker` checks that the message at that position still has that id. If it does not, it drops the extraction and logs at information.
- **Old anchors.** An anchor with no id (one queued before this) behaves as before.

## Acceptance
A worker test: queue an extraction, cut the tail, run the worker, and nothing is extracted.
