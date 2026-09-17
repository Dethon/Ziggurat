# Probe, 2026-09-18, jev-1.13.0

Inline scripts, run from the grilling session; the numbers are what the spec cites.

**One combined question per overlay kind** (cookie goal "fewest cookies, accept if that is all"):
10/12. Misses: `["Más información", "Aceptar y continuar"]` → none (0.38); `["Manage
preferences", "Got it"]` → *Manage preferences* (0.91) — a literal reading of "fewest cookies".

**Cookie walls as two questions** (`reject`: closes refusing optional cookies or keeping only the
necessary ones; `accept`: closes accepting or acknowledging; both say a control that opens settings
or more information does not close the wall), code taking a reject at confidence ≥ 0.6 first, else
the accept: 9/9 — `Rechazar todo`, `Entendido`, `Aceptar y continuar`, `Solo necesarias`,
`Got it`, none over `["Iniciar sesión","Buscar","Menú"]`, `Reject`, `OK`, `Continuar sin aceptar`.

Age (`Soy mayor de 18 años`, `I am 18 or older`), newsletter (`Quizás más tarde`, `×`),
notification (`Bloquear`): 5/5; none over `["Comprar ahora","Ver carrito"]`. Median 325 ms warm.

State shape: `{ "overlay_kind": "cookie", "controls": [{ "index", "role", "name" }…] }`;
criteria: one entry per index naming the control, plus `none`.
