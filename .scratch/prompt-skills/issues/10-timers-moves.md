# 10 — Timers moves

**What to build:** The timers section splits by the timing rule: choosing rules and their claims stay as a standing stub served by the mcp-timers server; the doing rules become one skill the same server publishes. The skill's description is a trigger claim cited by the timers scenarios, which require the load. Claims move with their prose and each cited body claim is demonstrated red with the description intact. The ceiling ratchets. If the stub would be under the size floor and the section has no choosing rules, the section moves whole and the stub is one sentence.

**Blocked by:** 09

**Status:** done

- [x] A full-tier eval pass runs on the base commit first and its scorecard is backed up.
- [x] The server serves the stub as its prompt and the skill as resources; agent snapshots show the stub and the advertisement; the body snapshot is under its budget.
- [x] The family's scenarios cite the trigger claim and require the load; mechanism scenarios still pass with no load required.
- [x] Every moved claim is cited or guarded as before; the commit notes each demonstrated red.
- [x] A full-tier pass after the move shows no claim rate below the backed-up scorecard; the ceiling is lowered to remaining plus headroom.
- [x] Spec: `.scratch/prompt-skills/spec.md` § Rollout.

Result (2026-09-08): base 73/73, 220/228 (`…after-sandbox-skill.json`); after the move 70/73, 220/228 (`…after-timers-skill-b.json`), one spurious load in 228. The timer family — the five timer scenarios, six voice timer turns, three mechanism turns, and the two timer turns in the delegation and memory families — is green, including "para la alarma que está sonando", red on the reflex in most of the day's passes, which now loads `countdown-timers` after the stub gained "whatever is ringing now is silenced through /timers". The first pass failed those two outside-family timer turns (no load permitted) and the ringing alarm (it loaded home-assistant); the stub sentence and both descriptions fixed it. The three reds after are the known flakers: the delegated research reply over five sentences, the attachment vault turn one glob over its ceiling, the urgent watch loading home-assistant once. Demonstrated red in `…timers-body-deleted.json`: no-satellite-asks-which-room 0/3 and ringing-is-stopped-by-dismiss 0/3 with the load on every run; the other twelve body claims stayed green without the body and became guards. Nabu 6,116 → 6,208 tokens (the skill list grew by one entry while the section shrank); ceiling 10,000 → 9,500.

