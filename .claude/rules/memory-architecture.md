---
paths:
  - "Infrastructure/Memory/**"
  - "Domain/Tools/Memory/**"
  - "Domain/Memory/**"
  - "Domain/Contracts/IMemory*.cs"
  - "Domain/Prompts/MemoryPrompts.cs"
  - "Agent/Modules/MemoryModule.cs"
---

# Memory Architecture

Built into the Agent process, not an MCP server:
- **Extraction** — `MemoryRecallHook` queues turns → `MemoryExtractionWorker` fetches the persisted thread and hands it to `ExtractionWindow.Build`, which cuts the window at the `MemoryAnchor` → `IMemoryExtractor` (LLM) reads it, rendered by `ExtractionWindow.Render` with `[CURRENT]`/`[context -N]` markers → `IMemoryStore` (Redis Stack, vector search) persists. Falls back to raw message content when the thread is unavailable.
- **Recall** — `MemoryRecallHook` runs before each turn: builds a user-only window, takes the extraction anchor, semantic-searches, attaches a `MemoryContext` to the message.

**A turn addressed to the Lemonade chat host is never extracted from.** The recall hook skips the enqueue when the message's config patch names a `lemonade/` model, beside the per-agent `memory` feature gate — the second reason nothing is written, stated where the first one is. It reads what the turn *asked for* rather than what served it, because the hook runs before the agent and no host has been chosen yet, so it fails closed. **Recall is deliberately not skipped with it**: the box may read what the agent already knew, and the boundary is one-way on purpose — do not "fix" it into symmetry. `docs/adr/0042-a-turn-addressed-to-the-local-box-is-never-extracted-from.md` records why, including why extraction is not simply routed to the local model.

**Embeddings are local.** `EmbeddingService` speaks plain OpenAI-compatible JSON against the Lemonade server already in the stack (`Memory:Embedding` — base address, model, dimension), which the container entrypoint pre-pulls and pins. There is deliberately **no fallback to a hosted provider**: its vectors are a different width and would be invalid against the index rather than merely slower, so a local failure degrades to a turn with no recall block and its own `memory-embedding` error metric. `docs/adr/0019-recall-embeds-locally-with-no-cross-provider-fallback.md` records why. The index dimension is configuration, not a constant, and `MemoryIndexVerification` refuses to start when it disagrees with the live index — at startup, because a lazy check would be swallowed by the recall hook's catch-all.

`Domain/Memory` owns both halves of what the model reads. `ExtractionWindow` cuts and renders the extraction window; the anchor it cuts at is only correct because recall runs before the turn is persisted, which `MemoryAnchor`'s factory names and `ChatMonitorMemoryAnchorTests` pins. Each rendered marker is cross-checked against the prompt constant that names it — renaming one side alone goes red.
- **Dreaming** — `MemoryDreamingService` periodically consolidates/prunes via `IMemoryConsolidator` (LLM).
- All three publish `MetricEvent`s.
