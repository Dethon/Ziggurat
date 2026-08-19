# Web: back, chaining, and the browse/snapshot judgement

Status: resolved

## Answer

Armed on 2026-08-19: back 3/3, the booking's two judged checks 4/4 each, and the wizard 4/4
after one real finding. On the first pass the wizard went 0/3 because the padrón page was
invisible: the model's query spells "padrón" with its accent, the query reaches the search
table url-encoded, and the ASCII keyword "padron" never matches — so the model improvised
and signed the user up on the wrong form. The keyword is now the prefix that survives
encoding ("padr"), the front page links the wizard, and the turn says "en la web del"
(the model had also rummaged the vault for a notebook called Cuaderno de barrio). The
encoding trap is pinned by a fixture test using the armed run's own query.

- `web.back-is-an-action` — the site gains a searchable front page; the user names the return
  ("vuelve atrás a la portada"), and the required call is web_action's back — a re-browse of
  the index url cannot satisfy it. The old exemption said no turn shape forces the return
  over remembering; naming the return moves the subject to *how* the return happens, which is
  what the claim states.
- `web.actions-chain-from-the-diff` — a new three-step wizard at /padron/alta: each step is
  hidden until the previous click, so the first snapshot shows only step one and every later
  ref exists only in a click's own diff. The ceiling (11) fits the flow with its single
  snapshot; snapshotting between every action does not. Fixture proven end-to-end in
  `EvalWebTests`, including that the revealed field's ref is readable from the click's diff.
  One trap found while proving it: the diff's `[after: ...]` header names the just-clicked
  button, so the two step buttons carry different labels (Siguiente / Continuar) — a lookup
  by "Siguiente" in step two's diff would find the disabled button it just clicked.
- `web.browse-reads-and-snapshot-structures` — a judged check on the booking scenario, which
  carries the richest call log: pass when each call serves a distinct purpose (browse with
  snapshot=true serves both at once), fail on a redundant browse/snapshot of the same page
  state.
