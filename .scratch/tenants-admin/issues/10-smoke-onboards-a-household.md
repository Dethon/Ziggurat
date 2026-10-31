# 10: The smoke tier onboards a household

**What to build:** A fresh household is onboarded end to end on a real stack: created from the dashboard, its owner signed in by the link, a key set against a stub provider probe, its agent named, a reply received, then suspended and refused. Spec: `../spec.md` (Testing Decisions, seam 5).

**Blocked by:** 02, 03, 05, 09

**Status:** ready-for-agent

- [ ] A household created on a fresh smoke stack gets a reply from its named agent
- [ ] Suspending it refuses its next turn
- [ ] The smoke suite passes on a fresh `scripts/smoke-stack.sh up`
