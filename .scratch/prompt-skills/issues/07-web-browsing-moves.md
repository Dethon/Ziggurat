# 07 — Web browsing moves

**What to build:** The web browsing section splits by the timing rule: choosing rules and their claims stay as a standing stub served by the mcp-websearch server; the doing rules become one skill the same server publishes. The skill's description is a trigger claim cited by the web browsing scenarios, which require the load. Claims move with their prose and each cited body claim is demonstrated red with the description intact. The ceiling ratchets. If the stub would be under the size floor and the section has no choosing rules, the section moves whole and the stub is one sentence.

**Blocked by:** 06

**Status:** done

- [x] A full-tier eval pass runs on the base commit first and its scorecard is backed up.
- [x] The server serves the stub as its prompt and the skill as resources; agent snapshots show the stub and the advertisement; the body snapshot is under its budget.
- [x] The family's scenarios cite the trigger claim and require the load; mechanism scenarios still pass with no load required.
- [x] Every moved claim is cited or guarded as before; the commit notes each demonstrated red.
- [x] A full-tier pass after the move shows no claim rate below the backed-up scorecard; the ceiling is lowered to remaining plus headroom.
- [x] Spec: `.scratch/prompt-skills/spec.md` § Rollout.

Result (2026-09-08): base 73/73, 221/228 (`…after-vault-skill-c.json`); after the move 72/73, 219/228 (`…after-web-skill.json`). The web family was green; the one red is "an unclear request is acted on" (a timer turn) at 1/3 on the load reflex reaching for `home-assistant` — 4 spurious loads in 228, the reflex the handoff accepts at ≤5%, on a scenario whose ceiling of 3 leaves no room for one. Demonstrated red in `…web-body-deleted.json`: urls-are-cited-only-in-writing 0/3 and partial-content-is-fetched-once 0/3 with the load on every run; refs-come-from-a-snapshot, type-reacts-and-fill-sets, actions-chain-from-the-diff stayed 4/4 and back-is-an-action 3/4 (its usual rate) and became guards. Nabu 9,212 → 8,065 tokens; ceiling 13,200 → 12,200.

