# Memory: the explanation, the expired fact, and the sweep

Status: resolved

## Answer

Armed on 2026-08-19: mechanism 3/3, expired fact 2/3, sweep 3/3 — the sweep deleted exactly
the three conversational scraps and left the three durable facts on every run.

- `memory.mechanism-is-never-mentioned` — "¿por qué nueve minutos?" after a nine-minute timer:
  the one turn that gives the model a reason to explain itself. The hard plumbing words are
  banned; "me dijiste" and "recuerdo" are ordinary speech and stay legal.
- `memory.outdated-facts-are-deleted` — "ya he vuelto del viaje": the flight fact has expired
  by the turn's own telling, nobody asked to forget, the deletion is the agent noticing. The
  coffee preference beside it must survive, which the store-after check says for free.
- `memory.noisy-store-is-swept` — reclassified from needs-fixture: the store is per-scenario
  (facts ride the turn), so a six-fact seed costs the other scenarios nothing — the old
  reason was wrong. Three durable facts against three conversational scraps, sweep asked for
  out loud; the forget tool's confirmation flow (query → candidates → memoryIds) is the path,
  and the survivors are the assertion.
