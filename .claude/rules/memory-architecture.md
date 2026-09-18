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
- **Extraction** — `MemoryRecallHook` queues turns → `MemoryExtractionWorker` fetches the persisted thread and hands it to `ExtractionWindow.Build`, which cuts the window at the `MemoryAnchor` and then drops tool-role messages and assistant messages that carry only a tool call, so the slots count conversation and a two-tool turn does not cost the window its context (nothing is rendered in their place: fetched text has no business in front of the memory writer) → `IMemoryExtractor` (LLM) reads it, rendered by `ExtractionWindow.Render` with `[CURRENT]`/`[context -N]` markers → `IMemoryStore` (Redis Stack, vector search) persists. Falls back to raw message content when the thread is unavailable.
- **Recall** — `MemoryRecallHook` runs before each turn: builds a user-only window, takes the extraction anchor, semantic-searches, attaches a `MemoryContext` to the message.

**A turn addressed to the Lemonade chat host is never extracted from.** The recall hook skips the enqueue when the message's config patch names a `lemonade/` model, beside the per-agent `memory` feature gate — the second reason nothing is written, stated where the first one is. It reads what the turn *asked for* rather than what served it, because the hook runs before the agent and no host has been chosen yet, so it fails closed. **Recall is deliberately not skipped with it**: the box may read what the agent already knew, and the boundary is one-way on purpose — do not "fix" it into symmetry. `docs/adr/0042-a-turn-addressed-to-the-local-box-is-never-extracted-from.md` records why, including why extraction is not simply routed to the local model.

**Embeddings are local.** `EmbeddingService` speaks plain OpenAI-compatible JSON against the Lemonade server already in the stack (`Memory:Embedding` — base address, model, dimension), which the container entrypoint pre-pulls and pins. There is deliberately **no fallback to a hosted provider**: its vectors are a different width and would be invalid against the index rather than merely slower, so a local failure degrades to a turn with no recall block and its own `memory-embedding` error metric. `docs/adr/0019-recall-embeds-locally-with-no-cross-provider-fallback.md` records why. The index dimension is configuration, not a constant, and `MemoryIndexVerification` refuses to start when it disagrees with the live index — at startup, because a lazy check would be swallowed by the recall hook's catch-all.

`Domain/Memory` owns both halves of what the model reads. `ExtractionWindow` cuts and renders the extraction window; the anchor it cuts at is only correct because recall runs before the turn is persisted, which `MemoryAnchor`'s factory names and `ChatMonitorMemoryAnchorTests` pins. Each rendered marker is cross-checked against the prompt constant that names it — renaming one side alone goes red.
- **Dreaming** — `MemoryDreamingService` periodically consolidates/prunes via `IMemoryConsolidator` (LLM).
- All three publish `MetricEvent`s.

**Three Jev judgments sit around the writers, and each fails toward what happens without it.** `MemoryJudge` (`Domain/Memory`) asks them through the one `IJudge`, over the window as fields (`context`, `current`), never the rendered string and never a tool result; the questions are worded as in `.scratch/jev-memory-judgments/probe/jev_memory_probe.py`, the bars in `Memory:Judgments` (`Agent/appsettings.json` alone) were measured there, and `MemoryJudgeJevTests` (`Category=Jev`) re-measures them against live Jev over the probe's synthetic sets — never a production conversation.
- **The gate**, in `MemoryExtractionWorker` after the window is built: three nouls (`fact`, `preference`, `instruction`); the extractor is skipped and the extraction event says `gated` only when every one is at or below the bar. Unsure, late, absent or disabled extracts as today, because a skipped memory is gone and a wasted call is a fraction of a cent. A Lemonade turn was never enqueued, so ADR 0042 covers Jev without a second gate.
- **The check**, in the worker between extraction and the store fan-out, per candidate in parallel: four nouls (`supported`, `about_user`, `durable`, `not_a_question`), stored only when each clears its own bar; an `Instruction` is judged on `supported` alone, because an instruction *is* a request. A drop publishes the candidate's text and scores; no answer stores as today; the 0.85 embedding dedup runs after, unchanged.
- **The pair relation**, in `OpenRouterMemoryConsolidator` after cosine clustering: one request per cluster, the memories by index and one four-way choice per pair (`same`, `updates`, `distinct`, `unrelated`) — no id a model could retype. Only the connected components of the `same`/`updates` links reach the merge model together, and `Consolidation.MergeableGroups` is what `MemoryDreamingService` checks before applying a `Merge` or `SupersedeOlder`: sources not all in one group are refused, logged by id and published on the dreaming event. A cluster Jev did not answer for goes as cosine made it and is applied unvetoed. Independently of Jev, a `Merge` with blank text is refused and its sources survive.
- One `MemoryJudgmentEvent` per call (`gate`/`verify`/`pairs`, answered or absent, none for an unconfigured judge), under `metrics:memory-judgment:`; the Memory page's Judgments tab lists them, and its breakdown charts outcome share, candidates, drops and refused merges.
