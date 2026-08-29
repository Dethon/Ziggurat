# 04 — Image fetch migrates per-ref

**What to build:** Fetching images runs through the new seam, one `OnRefAsync` run per image ref — no batch semantics enter the seam, so each image's wall (closed, superseded, unknown) falls out per ref exactly as today while the rest of the batch still returns bytes. The image-namespace stamping policy flows through the method the same way the element namespace does (separate counters and lease, per ADR-0033/0034). The hand-written "site refused and the page is closed" override disappears from the fetch path: the pipeline's post-work closed check (ticket 01) answers the closed wall regardless of what the work produced — assert that here for fetch.

**Blocked by:** 03 — By-ref intent: action and back migrate, popup folds in.

**Status:** done

- [x] Image fetch calls `OnRefAsync` per ref and performs no session bookkeeping of its own; the last old-verb call sites in the browser are gone
- [x] One closed tab walls only its own images; other refs in the same request still return bytes
- [x] Ordering fact: a fetch whose tab closed mid-work answers the closed wall even when the work reported a refusal (red first — replaces the third disambiguation spelling)
- [x] Image stamping uses the image namespace's counters and lease through the method's stamping policy
- [x] Existing suites stay green; nothing in the Playwright browser sequences the tab protocol any more

## Comments

Implemented 2026-08-29. The fetch drives `OnRefAsync` once per ref with `StampPolicy.None` (a
fetch stamps nothing; the image namespace's stamping continues to flow through the browse and
annotate intents, which carry `Restamp(Image)` — same lease machinery, same counters). The
hand-written "site refused and the page is closed" override is gone: the pipeline's post-work
closed check answers it, pinned at the browser seam by
`FetchImagesAsync_WhenTheTabClosedMidFetch_AnswersTheClosedWallNotTheSitesRefusal` (written green
against the old spelling, kept green across the migration). An empty-refs call still answers
SessionMissing for a dead session via the pool lifecycle `Get`, not a protocol verb.
