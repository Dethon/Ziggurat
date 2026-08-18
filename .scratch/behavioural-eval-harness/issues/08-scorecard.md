# 08 — Scorecard

**What to build:** a full pass leaves behind a record of how the current model did, per claim, so
that a model upgrade's effect on behaviour is a diff rather than an impression.

One JSON summary per full pass: the model, the timestamp, and the pass rate for every claim
exercised. It is not committed — a stochastic wobble must not dirty the working tree.

**Blocked by:** 03, 05.

**Status:** ready-for-agent

- [x] A full pass writes one summary into the same gitignored output directory as the dumps.
- [x] The summary names the model and provider actually served, not the configured one.
- [x] Every claim exercised in the pass appears with its observed rate; claims not exercised are
      distinguishable from claims that failed.
- [x] A smoke-tier run does not overwrite a full pass's summary.
