# 08 — Scheduling moves

**What to build:** The scheduling section splits by the timing rule: choosing rules and their claims stay as a standing stub served by the mcp-scheduling server; the doing rules become one skill the same server publishes. The skill's description is a trigger claim cited by the scheduling scenarios, which require the load. Claims move with their prose and each cited body claim is demonstrated red with the description intact. The ceiling ratchets. If the stub would be under the size floor and the section has no choosing rules, the section moves whole and the stub is one sentence.

**Blocked by:** 07

**Status:** done

- [x] A full-tier eval pass runs on the base commit first and its scorecard is backed up.
- [x] The server serves the stub as its prompt and the skill as resources; agent snapshots show the stub and the advertisement; the body snapshot is under its budget.
- [x] The one scheduling scenario (a mechanism turn) cites the trigger claim and requires the load; the other mechanism scenarios pass with no scheduling load. The stub declares two choosing claims, guarded by the same 2026-08-18 demonstration as their timer twins.
- [x] Every moved claim is cited or guarded as before; the commit notes each demonstrated red.
- [x] A full-tier pass after the move shows no claim rate below the backed-up scorecard; the ceiling is lowered to remaining plus headroom.
- [x] Spec: `.scratch/prompt-skills/spec.md` § Rollout.

Result (2026-09-08): base 72/73, 219/228 (`…after-web-skill.json`); after the move 70/73, 219/228 (`…after-scheduling-skill.json`), 2 spurious loads in 228. The scheduling scenario is green. The three reds are elsewhere and recurring: the ringing-alarm voice turn loading `home-assistant` (ticket 10's countdown-timers description names "ringing now"), the night-time watch editing the seeded watch instead of adding one (a watch body rule, 1/3–2/3 across the day's passes), and the delegated research reply at seven sentences against five. The section declared no claims, so the demonstrated red is informational only: with the body deleted the schedule was still written correctly 3/3 (`…scheduling-body-deleted.json`) — the file's shape is in the mount's description. The live agent list the server appends stays with the stub. Nabu 8,065 → 6,976 tokens; ceiling 12,200 → 11,000.

