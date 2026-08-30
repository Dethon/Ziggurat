# Spec — One label, one language

Status: ready-for-agent

Origin: `.scratch/architecture-review/report.md`, Candidate 2 ("One label ladder, two renderers"), grilled to a settled design on 2026-08-30. The grilling retired the candidate's title solution: nothing in-page consumes a label, so instead of one definition rendered twice there is one ladder in one language, and the page script answers only facts. ADR-0033 already carries the dated retirement note in its Consequences, and the glossary already defines **Label** — both amendments are in the working tree beside this spec.

## Problem Statement

The label ladder — the fallback sequence that picks the words a picture is known by — is written twice, in two languages: once in C# building the image entry, once in the fetch script naming the picture in the note that replaces it. What holds the two together is a comment and a rules-file paragraph, and that promise has failed on record (a bare slice where the entry ellipsised; a falsy short-circuit that blanked every rung after a whitespace-only first one; a cap move that had to touch both files). Worse, the grilling verified the two sides are *already* unequal in ways no commit ever chose: .NET and JS disagree about whether U+0085 and U+FEFF are whitespace, about `$` before a trailing newline in the filename gate, and the wire-raster media-type check trims on one side only — so the same page can label the entry and the note with different words today, silently. Both sides also share a live bug: the length cap cuts inside a surrogate pair, so a label ending in an emoji can leave a lone high surrogate — and produce different final bytes per side.

A second sequence hides in the same script: the byte-acquisition descent (wire fetch → canvas → context request → element screenshot) is split at the wrong place, three rungs in the JS string and two in C#, joined by a stringly-typed return protocol. The descent's ordering is therefore untestable without a browser, and three of its rungs are in practice covered only by tests against live third-party sites that skip themselves whenever the site has a bad day. Meanwhile the markdown converter carries a fallback numbering path that only hand-written test markup ever takes, so the unit suite exercises numbering production never runs — a divergence that has already shipped one real bug.

## Solution

Delete the in-page label computation. The fetch script shrinks to a probe that answers **facts** — the strings the page offers about a picture and what each acquisition attempt observed — and the one ladder in C# names both the image entry and the note, from the same rules, in the same words. The ladder becomes a pure function over a facts record, with one record built from the parsed page on the extraction side and one from the probe's answer on the fetch side. The engine divergences die structurally: there is no second engine.

The byte-acquisition descent becomes a module of its own, the same shape the tab protocol landed on: the module owns the ordering, each in-page rung is a separate probe call, and C# decides every step down. The stringly protocol is replaced by typed per-rung answers; the wire-raster rule is stated once. The descent's ordering becomes a millisecond unit test against a faked probe, and the live-site tests shrink to their one legitimate job — attesting that real CDNs still behave the way the hermetic twins encode.

## User Stories

1. As the model, I want the note that replaces a fetched picture to use the same words as the image entry I chose it by, so that the tool never reads as having fetched something else.
2. As the model, I want a picture whose page offers nothing to say to still get a label from its rendered dimensions, so that a list of entries is never a list called "image".
3. As the model, I want a whitespace-only description to fall through to the next rung rather than blanking the label, so that a page's sloppy markup does not erase a picture's name.
4. As the model, I want a label ending in an emoji to survive the length cap whole, so that a cut name never arrives as mojibake.
5. As the model, I want a label containing an exotic space to be flattened the same way in the entry and the note, so that the two never disagree by an invisible character.
6. As the model, I want a picture's bytes to arrive as the file the site served whenever any fetch can obtain them, so that fidelity is lost only when nothing else can reach the pixels.
7. As the model, I want a CDN that withholds sharing headers to still yield the picture through the page's own client or a screenshot, so that a guarded image costs fidelity rather than the turn.
8. As the model, I want an image that never loaded to answer its own wall rather than the site-refused one, so that I pick a different picture instead of retrying a dead link forever.
9. As the model, I want a picture the page never measured to get no entry and no ref, so that I am never offered a handle that cannot route.
10. As a maintainer, I want the label rules readable in one place in one language, so that "what is this picture called" is a file read, not a two-file diff.
11. As a maintainer, I want changing a label rule to be one edit that both the entry and the note obey, so that the two-sided edit hazard stops existing.
12. As a maintainer, I want the byte-acquisition ordering written once in the language the rest of the policy lives in, so that a descent change is reviewed as logic, not as a string.
13. As a maintainer, I want the wire-raster acceptance rule stated once and compared once, so that a header quirk cannot pass one copy and fail the other.
14. As a maintainer, I want the fetch script to carry no policy — only observations — so that reading it never requires cross-checking a C# twin.
15. As a maintainer, I want the converter's numbering to have exactly one path, so that unit coverage and production behaviour stop being different programs.
16. As a test author, I want to assert the descent's ordering against a faked probe ("wire fails, canvas taints, the context client answers"), so that acquisition logic tests run in milliseconds without a browser.
17. As a test author, I want the label rules unit-tested facts-in/label-out, including the Unicode and surrogate edges, so that the historical sync failures become named, cheap regression tests.
18. As a test author, I want hermetic twins of the CDN behaviours served by the test's own route fulfilment, so that a green run does not depend on what a third-party gallery published today.
19. As a test author, I want the live-site tests kept as an explicit external category with a skip on every third-party precondition, so that they attest reality without gating a bare test run on it.
20. As a test author, I want unit fixtures to stamp refs the way the page does, so that the suite exercises the numbering production actually runs.
21. As a reviewer, I want the migration to land as small green steps — label hoist, descent split, numbering fold, cut fix, tests and docs — so that any regression bisects to one commit.
22. As a prompt/rules author, I want the rules file to stop pledging that two copies move as one, so that the prose describes an invariant the code now holds by construction.

## Implementation Decisions

All settled in the grilling; none are open.

- **Facts-back, not two renderers.** The in-page label ladder is deleted, not generated. The probe returns the raw strings the page offers about the picture (its description, an associated caption, its advisory title, the words of an enclosing link, its source address, and the stamped rendered dimensions), and the C# ladder computes the label. This was chosen over emitting compensated JS from a single definition because there is no in-page consumer of the label — verified: every use is outbound — and because a generator emitting the already-divergent regexes verbatim would look solved while preserving the divergence.
- **One ladder, one shape.** The existing entry-building ladder is refactored into a pure function over a facts record. Two builders fill the record: the extraction side from the parsed page's elements, the fetch side from the probe's answer. Rung order, the data-URI exclusion, blank-not-falsy fallback, sanitising, and the cap all live in that one function.
- **The surrogate-safe cut.** The length cap becomes text-element-aware so it never splits a surrogate pair; landed red-first as its own step. The cap's value and the "a real label should never meet the cap" stance are unchanged.
- **The descent module.** A new internal class owns the byte-acquisition ordering, behind one narrow probe interface with a method per in-page rung (locate-and-facts, wire fetch, canvas read) plus the context-request and element-screenshot rungs; the Playwright-backed probe is its only production implementation. The browser keeps page choreography and loses acquisition policy — the same eviction the tab protocol just performed for session bookkeeping.
- **Per-rung calls, C# descent.** Each rung is a separate probe call and C# decides every step down. The whole sequence runs inside one locked tab work callback (the tab-protocol module, already landed), so the page cannot change between calls. The stringly return protocol is replaced by typed per-rung answers; the ladder module maps them to the existing fetch statuses, which are unchanged.
- **The credentials rule stands, verbatim.** Same-origin with the page's credentials, cross-origin anonymously — the big CDNs answer a wildcard, and a wildcard is exactly what a credentialed request is rejected against. This broke the first implementation; do not "fix" it.
- **The wire-raster rule is stated once.** One constant, one comparison, media type trimmed once — retiring the third duplicated constant and its one-sided trim.
- **The fallback numberer dies.** The converter's test-only counter is deleted. A picture with no stamped ref gets **no entry**: an unmeasured picture cannot pass the size gate and an unrouteable ref is a lie the model would act on. Unit fixtures stamp refs and dimensions the way the page does.
- **Unification is fixing.** Where the two ladders disagree today (the Unicode whitespace set, the filename gate's anchoring, the media-type trim), the C# behaviour is the surviving semantics, and each formerly-divergent edge gets a unit fact on the surviving ladder.
- **Documentation.** The rules file's "written twice and must move as one" paragraph is rewritten in the same change to what code cannot hold: the one-label invariant by name, the cap-is-a-safeguard rationale, the credentials trap. ADR-0033 and the glossary are already amended; no new ADR — the move is cheap to reverse and the decided behaviour (rung order, credentials, size filter, walls) is untouched.
- **Migration: five steps, one TDD triplet each, every step green.** (1) Hoist the label — probe answers facts, the unified ladder names the note, the in-page ladder is deleted. (2) Split the descent — the new module, the probe interface, per-rung calls, the single wire-raster rule. (3) Fold the numbering — delete the fallback counter, adopt the no-entry contract, stamp the unit fixtures. (4) The surrogate-safe cut. (5) Hermetic twins, the external-category tidy-up, and the rules-file rewrite.

## Testing Decisions

- **A good test drives a seam and asserts observable behaviour** — the label a caller receives, the fetch status and bytes, the entries a conversion emits — never the module's internals. The descent's ordering is asserted through the faked probe's answers, not by spying on call sequences beyond what the probe's own contract shows.
- **Unit surface, three families.** The ladder function, facts-in/label-out: every rung, the fallbacks, the sanitising, the cap, and the formerly-divergent Unicode edges plus the surrogate cut as named facts. The descent module against a faked probe: each step-down (wire fails → canvas; canvas taints → context request; context request refused → screenshot; never-loaded answers its own wall), the wire-raster acceptance, and the credentialed/anonymous choice. The converter: the no-entry contract for an unstamped picture, and the existing entry-shape facts on stamped fixtures.
- **Integration surface: hermetic twins first, live attestation second.** The route-fulfilment fake-CDN pattern already proven in the measurement tests gains twins for the rungs it does not yet cover (the same-origin credentialed fetch; the vector-image-leaves-as-pixels rule). The three live-site tests remain in the external category as reality checks only, and the one that hard-asserts navigation gains the same skip-on-unreachable courtesy as its siblings. Anything assertable only against a live site is a finding to bring back, not a reason to widen the live surface.
- **Prior art.** The measurement tests' route-fulfilment style for the hermetic twins; the tab-protocol unit suite's faked-seam ordering style for the descent module; the existing entry unit tests for the ladder facts, re-aimed at the unified function.
- **Red first.** Every semantic unification and the cut fix land as a failing test before the implementation moves, per the repo's Red-Green-Refactor rule.

## Out of Scope

- Every other report candidate.
- Any change to decided behaviour: the rung order, the size filter, the credentials rule, the wire-raster set's membership, ref namespaces and lifetimes, the fetch caps, the seven walls, truncation and paging. All stand as ADR-0033 decided them.
- The public browser contract and every layer above it — MCP tools, domain tools, prompts. No caller above the browser observes this change.
- Creating CI. The corrected premise is recorded below; wiring workflows is its own effort.
- A new ADR or further glossary changes — both amendments are already made.

## Further Notes

- The report's line anchors predate the tab-protocol migration and sit roughly 110 lines stale; this spec deliberately names no line numbers.
- Corrected premise from the grilling: the external-category tests are not filtered out of a bare test run (there is no CI in this repo); they evaporate via their own skips on a third party's bad day. The hermetic/live line already exists inside the measurement test file — this spec extends that line, it does not invent it.
- The verified divergence inventory (the Unicode whitespace set on U+0085/U+FEFF, `$` before a trailing newline in the filename gate, the one-sided media-type trim, the shared surrogate cut) lives in the grilling record; each item becomes a unit fact on the surviving ladder rather than a compatibility shim.
- The grilling settled fourteen decisions across three rounds; if an implementer hits a genuinely undecided point, it is new information, not a gap in this spec — surface it rather than guessing.
