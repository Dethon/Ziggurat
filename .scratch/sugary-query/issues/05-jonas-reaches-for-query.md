# 05 — Jonas reaches for query

**What to build:** Asked about her log, Jonas loads the sugary-log skill and answers with `query`
instead of reading files: the skill teaches the tables, `README.md` before a number, `query` for
every lookup and sum, and the sandbox with DuckDB or Polars over `/sugary/**/*.parquet` for
analysis, charts and joins with `/ha`. The rule against suggesting a dose is kept word for word. The
eval's two Jonas scenarios about her log expect it.

**Blocked by:** 03

**Status:** ready-for-agent

Spec: `.scratch/sugary-query/spec.md`

- [x] The sugary-log skill's description and body are rewritten around `query` and the sandbox; the dose rule and its claim are unchanged; the trigger claim reads "queried" instead of "read".
- [x] Prompt snapshot tests are updated for the skill, the mount description and the query tool.
- [x] The eval's "looking" permission includes `query`.
- [x] `HerLogIsReadThroughTheSkill` requires a `query` call after the skill loads, in place of a read under `/sugary/`.
- [x] `HerDosesAreReadNeverAdvisedOn` requires a `query` call whose `sql` argument names `doses`, through a new argument matcher on a text argument (the existing ones are path-shaped).
- [x] The eval's sugary log fixture serves the Parquet mount and answers `query`.
- [ ] A changed-tier armed run against `master` is green for both scenarios, or its reds are written up in this ticket with the dump's evidence.

## Comments

- The doses scenario's evidence is a `query` whose `sql` names `doses`, through the existing
  `Arg.Matches(name, pattern)`, which already matches any text argument: no new matcher was needed.
- The skill's examples use lentejas and 3 October, not paella and 9 August: the eval's seeded log
  answers a question about the last paella, and the skill must not hand a scenario its answer.
- The changed-tier armed run spends money and is run only when asked.
