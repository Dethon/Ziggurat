# 0041 — The changed tier runs what a diff touched

Status: accepted
Date: 2026-09-10

## Context

A prompt author's loop is edit, run, read the scorecard, edit again. The two tiers offered
were a full pass — 73 scenarios, the whole bill, to learn about the three an edit could have
broken — and the smoke tier, one canary per family, which says nothing about the section
being edited unless its canary happens to cite it. What people did in between was filter by
family class name, which works while the edit stays inside one family and overwrites the full
scorecard when it does not (the family run files itself as a full pass).

ADR-0031 already put the mapping in the tree: a claim is declared beside the prose that
teaches it, and every scenario cites the claims it tests. The file that spells a claim's id is
the file whose edit can break the claim.

## Decision

A third tier, `Changed`, whose rows are computed from the diff. `ZIGGURAT_EVAL_CHANGED=<ref>`
(`1` for `master`) compares the working tree against its merge base with the ref — the
branch's own commits, staged, unstaged and untracked files alike — and selects:

- every scenario that tests a claim declared in a changed file outside `Tests/`, where "tests"
  is the coverage test's own reading: cited, guarded, judged, or conditionally cited;
- every scenario named in a changed file under `Tests/Eval/Scenarios/`.

The selection is `ChangeScope.Select`, a pure function over the changed paths, a file reader,
the suite and the manifest; `EvalChanges` is the git plumbing around it, read once per process.
The tier's four shard classes take the selection as their rows, hold one row that skips when
the diff selected nothing for them (a theory with no rows fails discovery), and file their
scorecard as `scorecard-changed.json` — never over the full pass's.

## Consequences

Between edits a pass costs what the edit touched. The full tier is unchanged and stays the
gate before a merge; the changed tier answers "did this prompt edit break what it teaches" and
its scorecard, filed under its own name with `exercised` beside `declared`, cannot be read as
more than that.

What the tier does not see is stated rather than guessed at. A tool description, a fixture,
the harness, the agent definition and the model have no claims to be read through, so an edit
there selects nothing: run the family, or the full tier. A claim whose prose lives in one file
and whose declaration in another would be missed, which is the arrangement ADR-0031 forbids.

The selection reads the working tree, so a scenario deleted in an edit is not selected for a
file that no longer exists, and a claim renamed breaks the build before it can be selected —
the same compile-time guarantee the scenarios themselves rely on.
