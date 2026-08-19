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

**Status:** resolved

- [x] Pages and fixture proofs in EvalWeb/EvalWebTests (including that /archivo's markdown
      carries no edition url).
- [x] Two scenarios plus the judged check, exemption lines removed, coverage test green.
- [x] Armed runs pass at each scenario's policy: materials 4/4 first pass, booking 3/4 with
      both judged checks, back 3/4 after three fixture fixes.

## Findings from the armed runs

The materials form and the booking's new browse-vs-snapshot judged check passed straight away.
The back scenario took three fixture lessons, all mine:

- The archive's search keywords shared nothing with a natural query about the raffle, so the
  model's first search surfaced the chronicle instead and it spent twenty calls hunting —
  including four text-searches of the vault, because the turn said "el archivo" without saying
  it was a web page. Keywords now overlap the chronicle's on purpose; the turn says "la página
  del archivo … en la web".
- An action result carries the url it landed on, so sibling pages named after a pattern
  (rifa-2024 → rifa-2025) let the model guess the second and browse forward instead of going
  back. The editions are now at opaque, unrelated slugs.
- With those fixed the flow was exactly right — open, click, read, Back, click, read — and the
  run still failed because the required open demanded snapshot=true, a requirement borrowed
  from the booking whose claim it belongs to. This model opened plain and snapshotted
  separately, which back-is-an-action has no business forbidding. Dropped; 3/4.
