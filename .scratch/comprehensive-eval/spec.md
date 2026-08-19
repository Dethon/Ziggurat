# Comprehensive eval suite

**Goal.** Close the gap between what the prompts claim and what the eval measures, so a model
bump is a scorecard diff over everything that matters — guards included, judgments included —
and the remaining backlog is typed and countable.

**Where this comes from.** The 2026-08-19 findings run (`.scratch/findings-from-the-eval/`) and
the discussion after it: guard scenarios are invisible as rates, most declared claims sit in an
untyped exemption list, several claims are unfalsifiable as written, and four claim families
have no fixture that could witness them.

## The work, in order

1. **Per-scenario rates in the scorecard** (issue 01). `EvalScorecard` already receives every
   scenario and its result; a `scenarios` section beside `claims` makes guards diffable.
2. **Typed exemptions** (issue 02). Each exemption carries a kind — `guard`, `needs-fixture`,
   `unfalsifiable`, `judge` — and the scorecard says per claim how it is covered.
3. **Unfalsifiable claims rewritten** (issue 03). A claim is a falsifiable statement; the ones
   that are not get restated as what they actually guard, or trimmed to the half a run can see.
4. **Judged checks in the full tier** (issue 04). A scenario carries judged expectations beside
   its deterministic ones; the full tier runs them, a rubric verdict fails a run like any other
   check, and the scorecard marks those rates as judged. No new tier.
5. **EvalWeb pages for the web backlog** (issue 05). A page past the browse limit and an
   inline-JS autocomplete — offline boundary intact.
6. **FakeHomeAssistant area whose slug and display name disagree** (issue 06), plus an action
   that takes the area id.
7. **Multi-turn scenarios** (issue 07). Scripted prior turns seeded into the thread, unlocking
   the "needs a conversation" exemptions.
8. **Sandbox as a testcontainer** (issue 08). The exec claims are unwitnessed because nobody
   will run model-authored shell on the host; a disposable container makes isolation the
   fixture's property.

Every issue lands with deterministic tests green; the scenarios each issue adds are validated
with a filtered armed run at the end, the way the findings fixes were.

## Non-goals

- Memory retrieval quality stays out of scope (ADR-0030); `EvalMemory` stays a fake.
- No forbidden-call lists (rejected in ADR-0030's design).
- The judge does not grade deterministic checks; it exists only for claims that are judgments.

## Validation, 2026-08-19

Armed run over the smoke tier plus every scenario this effort added or changed, against
`openai/gpt-5.6-luna` (OpenAI) with the judge live:

- Smoke tier: 10/10 with the sandbox newly mounted in every stack — no regression from the
  larger toolset.
- Judged checks: timer id 3/3, no-step-narration 4/4, surgical edit 1/1 — the judge returned
  parseable verdicts on every run.
- New scenarios: checksum-from-the-sandbox 4/4, area-slug 3/3, context-bound summary 3/3,
  chronicle 3/3 (after rephrasing as an instruction), autocomplete 3/4 (after the keyup fix and
  tolerating the recorded delegation reflex).

Two findings fed back into fixtures: a Playwright fill fires input events (pages meant to
"react to keystrokes" must listen to key events), and research-phrased turns delegate — both
now written where they bit.
