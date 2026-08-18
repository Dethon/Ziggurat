# 03 — Run policy and failure dump

**What to build:** a scenario survives a stochastic model without hiding regressions, and a
failure explains itself without being re-run.

A scenario declares how many of how many runs must pass and which tier it belongs to. Runs stop
as soon as the threshold is unreachable and report the honest partial rate. Nothing retries until
green — half the assertions in this suite are negative, and retrying past a failure hides exactly
what it is meant to catch.

A failed run writes one self-contained file into a gitignored output directory: the assembled
system prompt, the model and provider actually served, the decorated turn, every recorded call
with arguments and results, the final reply, and which assertion failed. Its path goes into the
assertion message. Successes are not archived.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] A scenario declares its threshold and tier; the smoke tier runs one canary per family once.
- [ ] A scenario whose threshold becomes unreachable stops early and reports what it observed,
      not a fabricated rate.
- [ ] A passing scenario writes no files.
- [ ] A failing scenario writes a dump containing every element listed above, and names its path
      in the failure message.
- [ ] The output directory is gitignored.
- [ ] Threshold arithmetic, early stop and dump contents are proven against a scripted chat
      client, with no model involved.
