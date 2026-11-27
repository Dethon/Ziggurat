---
paths:
  - "Infrastructure/Memory/**"
  - "Domain/Tools/Memory/**"
  - "Domain/Memory/**"
  - "Domain/Contracts/IMemory*.cs"
  - "Domain/Prompts/MemoryPrompts.cs"
  - "Domain/Prompts/Sections/memory.md"
  - "Agent/Modules/MemoryModule.cs"
---

# Memory Architecture

Built into the Agent process, not an MCP server:
- **Extraction** — `MemoryRecallHook` hands the turn's extraction back (`MemoryExtraction`, its anchor read before the turn is persisted) and the conversation group queues it **once the turn worked**: a turn refused for its household's key or credit, or a model error, is extracted from nothing (its retry is extracted in its place); a reroll's turn and a Continue are never extracted. `MemoryExtractionWorker` → `ExtractionWindow.Build` (cut at the `MemoryAnchor`; a worker finding another entry at `MemoryAnchor.LastEntryId` — the conversation was rewritten — extracts nothing; tool traffic and Continue's ask dropped) → `IMemoryExtractor` → `IMemoryStore` (Redis Stack, vector search). Falls back to the raw message content when the thread is unavailable.
- **Recall** — `MemoryRecallHook` runs before each turn and attaches a `MemoryContext`. The window is embedded as a **query** (`MemoryRecallHook.QueryInstruction`) while stored memories, extraction's duplicate check, dreaming's merge and the forget tool's search stay **bare** — keep both sides as they are. A result is kept only at `Memory:Recall:MinSimilarity` (0.40) against the **raw cosine** (`MemorySearchResult.Similarity`), never against `Relevance`. The instruction and the floor were measured together (2026-10-26): change either and re-measure. A memory below the floor is not recalled, so its access time is not refreshed and decay reaches it — intended.
- **Dreaming** — `MemoryDreamingService` consolidates/prunes via `IMemoryConsolidator`.
- All three publish `MetricEvent`s.

## Whose memory

**Memory is a person's, keyed by the account the turn acts for.** Every entry (`memory:{userId}:{memoryId}`) and the profile (`memory:profile:{userId}`) is filed under the turn principal's `Account.UserId`, **never under `Sender`** (a label another person can share). One person has one memory across Telegram, the chat client and voice. `IMemoryRecallHook.EnrichAsync` takes an `Account?` (handed by `ConversationGroup` off `Principal.Account`) so no caller can hand it a name; search, profile, access refresh, extraction request and recall metric are keyed by it.
- `memory_forget` reads the account of the turn **in flight**, per call (`Principal.Of(FeatureConfig.ConversationContextProvider())`), **never `FeatureConfig.UserId`** — the tool set was built by the conversation's first turn, and a later one may be somebody else's.
- **No account, no memory.** A turn with no account (the voice guest `Principal(voice, "household")`, a channel not yet on accounts) recalls nothing, enqueues nothing, publishes no recall event; `memory_forget` refuses it as an authentication error. **No fallback to the sender or a shared bucket**: a guest must reach nobody's memory.
- **A fire reads as its writer and is never extracted from.** A turn with an `OriginKind` (schedule or watch fire, login wake) carries the principal of the turn that set it going, so it recalls that account's memory, but enqueues no extraction — keying by account must not widen extraction to a person's bucket. The hook reads it off the turn's `ConversationContext`, which the group stamps before asking for recall.
- **Old keys are not read.** Channel-name buckets from before accounts are merged onto user ids by the household migration (`HouseholdMigration/Steps/MemoryBuckets.cs`, `.claude/rules/household-migration.md`); recall reads the one account bucket and never falls back to them.
- **Dreaming** walks every household and its people (the accounts service's `people` view) on each household's key; an id in no household is left alone (`.claude/rules/household-config.md`, "Who pays"). It **skips a suspended household and a disabled member** (`HouseholdAgents.Suspended`, `AgentHouseholdConfig.DisabledPeople`, from the agent view the household's turns are refused by); extraction of a turn admitted before the disabling skips them by the same view. Their memories are left as they are.
- Token metrics: extraction's model calls are attributed to the account id (`OpenRouterMemoryExtractor` stamps it as sender id) and the agent whose conversation they read (`SetAgentId`), while the turn's are attributed to the sender's display name, so `tokens:byUser:` shows one person under both until token metrics follow the account.

## Boundaries

- **A turn addressed to the Lemonade chat host is never extracted from.** The recall hook skips the enqueue when the config patch names a `lemonade/` model (beside the per-agent `memory` feature gate), reading what the turn *asked for*, so it fails closed. **Recall is deliberately not skipped with it** — the boundary is one-way on purpose; do not "fix" it into symmetry. `docs/adr/0042-a-turn-addressed-to-the-local-box-is-never-extracted-from.md`.
- **A built app's ask is neither recalled for nor extracted from.** `MemoryRecallHook` returns nothing for a message carrying an app turn (`ChatMessage.IsAppTurn`, the stamp's presence, so one it cannot read still fails closed), before the agent is even looked up. A gate of its own: it is not the Lemonade boundary and does not make that one symmetric.
- **Embeddings are local.** `EmbeddingService` against the Lemonade server in the stack (`Memory:Embedding`). **No fallback to a hosted provider** — its vectors are a different width — so a local failure degrades to a turn with no recall block and a `memory-embedding` error metric (`docs/adr/0019-recall-embeds-locally-with-no-cross-provider-fallback.md`). The index dimension is configuration; `MemoryIndexVerification` refuses to start when it disagrees with the live index, at startup because a lazy check would be swallowed by recall's catch-all.
- `Domain/Memory` owns what the model reads: `ExtractionWindow` cuts and renders (`[CURRENT]`/`[context -N]`). The anchor is correct only because recall runs before the turn is persisted (`MemoryAnchor`'s factory, `ChatMonitorMemoryAnchorTests`). Each rendered marker is cross-checked against the prompt constant that names it — rename both sides together.
- **Every memory model call is its household's**: extraction (gate, extractor, check) and dreaming (pair relation, merge, profile) take a `ModelPayer` and ask through `PaidChatClients` on its key, counted under its household. Extraction settles the payer before the gate and extracts nothing for a household with no key or one it cannot read just now.

## Jev judgments

**Three Jev judgments sit around the writers, and each fails toward what happens without it** (unsure, late, absent or disabled = extract, store, merge as before). `MemoryJudge` asks them through `IJudge` over the window as fields (`context`, `current`), never the rendered string, never a tool result. Questions are worded as in `.scratch/jev-memory-judgments/probe/jev_memory_probe.py`; the bars in `Memory:Judgments` (`Agent/appsettings.json` alone) were measured there, and `MemoryJudgeJevTests` (`Tests/Eval/ModelChecks/`) re-measures them on the probe's synthetic sets — **never a production conversation**.
- **The gate** (`MemoryExtractionWorker`): skips the extractor (event says `gated`) only when every noul is at or below its bar. A Lemonade turn was never enqueued, so ADR 0042 covers Jev without a second gate.
- **The check** (worker, per candidate): stored only when each noul clears its bar; an `Instruction` is judged on `supported` alone. **A candidate's text is published with its scores for the home alone** (`MemoryJudgmentContext.ShowsContent`, from the agent view's `IsHome`; a context that says nothing shows nothing): the Judgments tab is the operator's. The 0.85 embedding dedup runs after, unchanged.
- **The pair relation** (`OpenRouterMemoryConsolidator`, after cosine clustering): memories by index, no id a model could retype. Only `same`/`updates` components reach the merge model together, and `Consolidation.MergeableGroups` is what `MemoryDreamingService` checks before applying a `Merge` or `SupersedeOlder` — sources not in one group are refused, logged and published. A cluster Jev did not answer for goes as cosine made it and is applied unvetoed. A `Merge` with blank text is refused regardless of Jev.
  - **Answered means every pair**: a partial or unknown-relation answer is an absence, and the chunk goes as cosine made it.
  - **Chunking (`Pairs:MaxClusterMemories`) happens only when the judge is enabled**; with no Jev an oversized cluster goes whole (chunking anyway made split duplicates unmergeable every night). A trailing chunk of one folds into the one before; a cap below two is the cluster whole.
- One `MemoryJudgmentEvent` per call (`gate`/`verify`/`pairs`, answered or absent, none for an unconfigured judge) under `metrics:memory-judgment:`; `MemoryJudgmentEvent.Candidate` is the home's alone, its scores, category and verdict shown for every household.
