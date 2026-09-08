# 07 — Web browsing moves

**What to build:** The web browsing section splits by the timing rule: choosing rules and their claims stay as a standing stub served by the mcp-websearch server; the doing rules become one skill the same server publishes. The skill's description is a trigger claim cited by the web browsing scenarios, which require the load. Claims move with their prose and each cited body claim is demonstrated red with the description intact. The ceiling ratchets. If the stub would be under the size floor and the section has no choosing rules, the section moves whole and the stub is one sentence.

**Blocked by:** 06

**Status:** ready-for-agent

- [ ] A full-tier eval pass runs on the base commit first and its scorecard is backed up.
- [ ] The server serves the stub as its prompt and the skill as resources; agent snapshots show the stub and the advertisement; the body snapshot is under its budget.
- [ ] The family's scenarios cite the trigger claim and require the load; mechanism scenarios still pass with no load required.
- [ ] Every moved claim is cited or guarded as before; the commit notes each demonstrated red.
- [ ] A full-tier pass after the move shows no claim rate below the backed-up scorecard; the ceiling is lowered to remaining plus headroom.
- [ ] Spec: `.scratch/prompt-skills/spec.md` § Rollout.
