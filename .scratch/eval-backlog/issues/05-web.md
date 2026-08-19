# 05 — Web: two pages and a judged check

- `back-is-an-action` — a raffle archive: `/archivo` lists two editions behind JS buttons
  (no hrefs anywhere in the markdown, so the edition urls cannot be harvested and browsed
  directly), each edition page carries its own total, and the task needs both. The only way to
  the second edition is back-then-click; the required call is a web_action with action `back`,
  and the reply must carry both totals.
- `actions-chain-from-the-diff` — a form with more steps than the booking: `/taller/materiales`,
  four fields and a submit (six calls straight through with the opening browse). The ceiling is
  set so that a snapshot between every action breaks it while a worker detour and one stray
  snapshot survive. The confirmation code proves submission.
- `browse-reads-and-snapshot-structures` — a judged check on the booking scenario: the judge
  reads the call list and fails a flow that fetched the same page twice for one purpose
  (a browse followed by a standalone snapshot of the same page before any action).

**Status:** ready-for-agent

- [ ] Pages and fixture proofs in EvalWeb/EvalWebTests (including that /archivo's markdown
      carries no edition url).
- [ ] Two scenarios plus the judged check, exemption lines removed, coverage test green.
- [ ] Armed runs pass at each scenario's policy.
