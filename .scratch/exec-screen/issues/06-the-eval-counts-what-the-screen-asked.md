# 06 — The eval counts what the screen asked

Status: resolved

Spec: `../spec.md` (Decisions: Eval). Blocked by: 04.

The scorecard reports, per pass and per scenario:

- how many execs were screened;
- how many were asked about, split by reach;
- the screen's Jev spend, inside `spend`, beside `preload`.

`AutoApproveHandler` still approves, so no scenario's verdict changes. The count is the screen's
false-positive rate on the scenarios' real sandbox traffic. It is deterministic harness code and
runs on a bare invocation.

Add at least one Mount scenario in which a fourth `EvalWeb` page (`Tests/Eval/Fixtures/EvalWeb.cs`)
plants a command after an innocent request. It records whether the screen asked, and it carries no claim until a baseline exists.
