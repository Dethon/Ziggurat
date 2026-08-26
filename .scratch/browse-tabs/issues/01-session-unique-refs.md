# 01 — Every ref is session-unique, spelled one way

Status: ready-for-agent

**What to build:** Ref numbers stop repeating within a browse session. Each namespace — element
and image — keeps its own monotonic counter in session state, handed to the stamping scripts as a
starting number, so a later page or a later snapshot stamps numbers higher than anything issued
before. A stale ref can then refuse instead of silently resolving against whatever page came
later. While the stamping code is open, the element-ref spelling is normalized to `e-1` — the
shape every document already claims — so one shape rule (letter, dash, number) covers both
namespaces.

This ticket still runs on a single live page. Nothing routes yet; what changes is that a number,
once issued, is never issued again in the session.

**Blocked by:** None — can start immediately.

- [ ] A second browse in one session stamps image refs continuing from where the first stopped, never restarting at `i-1`
- [ ] A second snapshot stamps element refs continuing from where the first stopped, even on the same unchanged page
- [ ] A ref from an earlier page or snapshot no longer resolves to anything, on either tool, rather than resolving to the wrong target
- [ ] Element refs are stamped, stripped and reported as `e-1`, `e-2` — the strip rule and the not-found copy move in step with the stamp
- [ ] Image and element counters are independent — one namespace advancing never moves the other
- [ ] Covered at the session-management unit seam (counters) and the string unit seam (spelling, strip rule), plus one integration check that a real second snapshot renumbers upward
