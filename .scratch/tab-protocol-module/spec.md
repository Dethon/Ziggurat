# Spec — Give the tab protocol a module

Status: ready-for-agent

Origin: `.scratch/architecture-review/report.md`, Candidate 1 (top recommendation), grilled to a settled design on 2026-08-29. Governing decision record: ADR-0034 — no decided behaviour changes; this makes its aspirational sentence ("`PlaywrightWebBrowser` only carries pages around") true.

## Problem Statement

Roughly eleven of the last thirty `fix(web):` commits share one cause: a call site forgot part of the tab protocol. The protocol — route or acquire, lock, re-check the page is still open under the lock, touch, read the URL before the work, do the work, mark the tab navigated when it moved, note the final URL, stamp refs under a lease with the right supersede flag, and tell "this tab died" apart from "the connection died" — is the real interface of the browse session's tab pool, but it exists only as prose: scattered comments and a rules-file paragraph. Every browse operation re-derives the ordering by hand, and the three copies have already drifted (three spellings of the closed-page re-check, three different conditionals guarding supersede, two spellings of the closed-vs-disconnected disambiguation).

The unit tests cannot catch these bugs structurally: they drive the protocol verbs directly, so they *are* the caller — they perform the sequencing instead of checking it. They stayed green through all eleven sequencing fixes. Integration tests compensate by reaching past the seam through a test-only accessor, the standard tell that the interface cannot express what needs asserting.

## Solution

Move the seam. The pool module stops exposing nine verbs the caller must sequence and instead exposes one method per operation intent. A caller states what the operation *is* — a browse of a URL, a call routed by a ref, a ref-less call on the current tab, a call addressed to a tab by URL — hands over the work to do on the page, and receives either the work's result or a named wall. The module owns the entire ordering, once. The Playwright layer shrinks to page choreography (goto, wait, dismiss, extract, act, screenshot) with no session bookkeeping in it.

A whole class of sequencing bug then becomes impossible to write at a call site, and the ordering itself becomes unit-testable in milliseconds against a faked page.

## User Stories

1. As the model, I want a re-browse of the same URL to supersede that tab's element refs, so that stale refs never route into a freshly reloaded DOM.
2. As the model, I want acting on a page to leave its refs alive when the URL does not move, so that I can fill a form a field at a time (per ADR-0034).
3. As the model, I want an action that navigates the page to supersede the old refs, so that the walls tell me to snapshot again instead of letting me act on ghosts.
4. As the model, I want a ref whose tab was quietly evicted to answer the closed wall with the tab's URL, so that asking again is ordinary housekeeping rather than an error.
5. As the model, I want a ref from before a page was replaced to answer the superseded wall, so that I know my coordinates aged out rather than the site failing.
6. As the model, I want a tab evicted in the instant between routing and locking to still answer the closed wall, so that a race never surfaces as a crash or a wrong-page answer.
7. As the model, I want one tab's death to be told apart from the whole browser connection dying, so that a single evicted tab never tears down every browse session.
8. As the model, I want an action that opened a popup to answer from the popup, so that "click the link" works the same whether the site opens in place or in a new page.
9. As the model, I want a browse into a session that is churning tabs faster than it can hold one to come back as a retryable answer, so that I can simply try again.
10. As the model, I want each image ref in a fetch judged on its own tab, so that one closed tab walls only its own images and the rest still return bytes.
11. As the model, I want a snapshot addressed to a URL whose tab is gone to be refused with the named wall, so that a composed answer is never silently built from the wrong page.
12. As a maintainer, I want the tab protocol readable top to bottom in one module, so that "what happens to a tab when a call touches it" is a file read, not an archaeology dig across comments.
13. As a maintainer, I want each protocol step written exactly once, so that a fix lands everywhere at once instead of in one of three drifted copies.
14. As a maintainer, I want adding a new browse operation to force a deliberate decision inside the module, so that a new call site cannot quietly skip protocol steps.
15. As a maintainer, I want the lock-ordering invariant (never the pool gate while holding a tab lock; a popup lock may nest inside the acting tab's lock because a popup is always a fresh page) held by the module, so that prose stops being the only guardian.
16. As a maintainer, I want the protocol's tuning constants (the acquire retry bound, the ref-resolution timeout) to live with the policy they belong to, so that the caller carries no policy fragments.
17. As a test author, I want to assert protocol ordering against a faked page ("an eviction landing between routing and locking answers the closed wall"), so that the eleven historical sequencing bugs become millisecond unit tests.
18. As a test author, I want every pool state change observable through walls and results, so that integration tests need no test-only accessor into the pool.
19. As a test author, I want the policy facts (LRU, reuse, ranges, tombstones, counters) to keep their coverage through the new seam, so that the move loses no existing assurance.
20. As a reviewer, I want the migration to land one operation at a time with each step green, so that any regression bisects to one small commit.
21. As a prompt/rules author, I want the rules file to point at the module instead of restating its ordering, so that the prose cannot drift from the code again.

## Implementation Decisions

All decisions below were settled explicitly in the grilling session; none are open.

- **Module identity.** `BrowserSessionManager` keeps its name and absorbs the protocol — no new fronting type. ADR-0034 and the web-browsing rules already name it as the policy home.
- **Interface shape: four per-intent methods, no intent object.** One public method per operation intent — browse by URL, routed by ref, ref-less on the current tab, addressed by URL (the composed-snapshot exception) — sharing one private pipeline. The intent *is* the method, so each signature carries exactly its own parameters; the closed set survives as "adding a method is deliberate". Flags and open composition were explicitly rejected: they hand sequencing knowledge back to callers.
- **Image fetch granularity.** One routed-by-ref run per image ref. Walls fall out per image exactly as today; no batch semantics enter the seam.
- **The work callback.** Work receives a module-owned tab-work context exposing the page, the URL as it was before the work, and — when the method's stamping policy says so — the stamp start number. The module opens the ref-stamp lease before the work and commits it after with the work-reported ref count; the lease, its supersede flag, and the counter locks never cross the seam. The page handle is the raw Playwright page (a narrower wrapper would converge on re-declaring it); convention, not the type system, keeps it from escaping the lock.
- **Result shape.** Every method returns an outcome union: ran-with-result, or a named wall — no session, superseded (with URL), closed (with URL), unknown (range aged out), acquire exhaustion, addressed-tab gone. Acquire exhaustion stops being a control-flow exception and becomes the retryable wall it always was. Callers map walls to their DTO statuses in one switch per operation.
- **Closed-during-work check.** The module re-checks page-closed after the work returns and overrides the work's answer with the closed wall — unifying the third, status-based spelling of the disambiguation with the two exception-based ones.
- **Supersede rule, intent-parametrized.** Browse always supersedes (a same-URL re-browse reloads the DOM; old refs must die). All other intents supersede iff the URL moved across the work — same-URL client-side re-renders deliberately keep refs alive, per ADR-0034's "acting does not unmake handles". Stated once, in the module.
- **Popup adoption folds into the action path.** The module owns pending-popup pickup timing and the nested popup lock with its no-cycle invariant; the action's outcome says whether a popup answered. The browser's only remaining popup duty is registering the page's popup event.
- **Unification is fixing.** Where today's three call sites disagree, the module encodes the correct semantics once and the divergent sites are treated as latent bugs — each unification gets its own failing test first, per repo TDD rules.
- **Perimeter.** The nine protocol verbs go private. The tab, routing-union, acquisition, and stamp-lease types go internal. Pool lifecycle members (create/get/close/clear/prune/dispose) stay public — they are pool lifecycle, not tab protocol. The test-only pool accessor on the browser is deleted.
- **Migration.** One operation at a time, navigate first (richest protocol), one commit per TDD triplet; the verbs stay public-but-shrinking until the last operation flips. The final commit seals the perimeter, deletes the test-only accessor and the seam reaches, and trims the rules file.
- **Documentation.** The web-browsing rules file's tabs section is rewritten in the same change to point at the module, keeping only what code cannot hold (settings, wall semantics for prompt authors). No new ADR — ADR-0034 already asserts this shape and the move is reversible. The domain glossary is untouched: its browse-session/tab/ref entries are model-facing and unchanged by an implementation move.

## Testing Decisions

- **A good test drives the public seam and asserts observable behaviour** — the outcome union, the walls, the results the model would see — never the module's internals. The prior unit suite failed precisely because it performed the sequencing itself; the rewrite must not recreate a test that is the caller.
- **Unit surface: the four per-intent methods against faked pages.** Two families:
  - *Policy facts* — the existing ~36 (LRU, reuse-before-open, ranges, tombstones, counters, idle pruning) ported to drive the new methods.
  - *Ordering facts* — new: each of the eleven historical sequencing bugs encoded as a fact (eviction between routing and locking answers the closed wall; a non-navigating action leaves earlier ranges routing; a same-URL re-browse supersedes; the closed wall wins over the work's own answer; exhaustion surfaces as its wall, not an exception; popup answers flow through the action outcome).
- **Integration surface: the browser contract, inferring pool state from walls and results.** A superseded ref answers its wall; a re-browse reports reuse; the fourth browse evicts the least-recently-touched tab, observable because that tab's refs answer closed. Anything genuinely unobservable this way is a finding — an interface gap to close — never a reason to reintroduce a backdoor accessor.
- **Prior art.** The existing pool policy unit tests (faked-page style) for the unit family; the existing tab-behaviour integration tests for the contract family. Both suites already exist and are being re-aimed, not invented.
- **Red first.** Every semantic unification lands as a failing test before the implementation moves, per the repo's Red-Green-Refactor rule.

## Out of Scope

- Candidate 2 (the label ladder), candidate 5 (the body window) and every other report candidate — candidate 2 shares files with this one and deliberately goes second.
- Any change to decided browsing behaviour: three-tab cap, LRU eviction, session-unique refs per namespace, the two walls plus the bounded-registry unknown, the composed-snapshot exception, reuse-before-open, the idle clock. All stand as ADR-0034 decided them.
- The public browser contract (`IWebBrowser`) and every layer above it — MCP tools, domain tools, prompts. No caller above the browser observes this change.
- Making the CI-evaporating live-site image tests hermetic (that belongs to candidate 2's territory).
- A new ADR or glossary changes.

## Further Notes

- The full evidence base (verified counts, line anchors, churn analysis) lives in `.scratch/architecture-review/report.md` under Candidate 1; this spec deliberately omits line numbers since the migration will move them.
- The grilling settled fourteen decisions across four rounds; if an implementer hits a genuinely undecided point, it is new information, not a gap in this spec — surface it rather than guessing.
- Expected mass shift: the pool module grows slightly in mass and shrinks hard in perimeter (~47 public members to the four methods plus lifecycle); the browser loses roughly 250 lines and all session bookkeeping.
