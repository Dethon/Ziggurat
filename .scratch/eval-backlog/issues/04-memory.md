# 04 — Memory: the three that needed a turn shape, not a fixture

- `mechanism-is-never-mentioned` — the exemption's own prescription: a turn that gives the model
  a reason to explain itself. History carries a pasta timer set from a remembered preference;
  the turn asks "¿y por qué nueve minutos?". The reply must answer without naming memory,
  storage or the recall block.
- `outdated-facts-are-deleted` — a fact whose expiry is legible from the turn itself: the trip
  the store says is being prepared is over ("ya he vuelto de Lisboa"). The stale fact must go,
  the unrelated one must survive, and nothing about it is said aloud.
- `noisy-store-is-swept` — the needs-fixture reason no longer holds: `Remembered` seeds are
  per-scenario, so a noisy store in this one scenario costs the others nothing. Six facts, four
  of them obvious extraction noise, an explicit "limpia mi memoria" turn; the four noisy ones
  are declared Forgotten and the two real ones must survive. The forget tool's confirmation
  step (query reaching >1 returns candidates) means the sweep may take a confirmation
  round-trip — the ceiling allows for it.

**Status:** resolved

- [x] Three scenarios written, exemption lines removed, coverage test green.
- [x] Armed runs pass at each scenario's policy — all three 3/3 on the first pass.

## Answer

The family closed without a single harness or prompt change. The why-question kept the plumbing
out of the answer on every run; the returned-from-Lisbon turn deleted the stale project and left
the coffee preference standing; and the sweep took exactly the four noise facts and kept the two
real ones, working through the forget tool's confirmation step where its query reached more than
one memory. The old needs-fixture reason for the sweep was simply stale: `Remembered` seeds are
per-scenario, so a noisy store in this one scenario costs the other scenarios nothing.
