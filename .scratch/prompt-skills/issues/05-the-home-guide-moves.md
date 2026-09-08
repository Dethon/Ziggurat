# 05 — The home guide moves

**What to build:** The rest of the Home Assistant split, on the path ticket 02 proved. The home guide splits by the timing rule: the standing stub keeps scope, the choosing rules and their claims (which mechanism; alarms versus timers versus watches versus schedules; area slugs are ids), and says to load the `home-assistant` skill and read the setup index in the same turn. The skill body takes layout, workflow, reading results, history, alarms, music and notes, and says to re-read the setup index when a name does not resolve. The home server publishes it beside the watches skill. The description is a trigger claim cited by the home and music families, which require the load; claims move with their prose and each cited body claim is demonstrated red with the description intact. The ceiling ratchets. Ask "turn off the kitchen light" and the model loads the skill, reads the index and turns off one light and nothing else, as today.

**Blocked by:** 02, 03, 04

**Status:** done

- [x] A full-tier eval pass runs on the base commit first and its scorecard is backed up.
- [x] The home server serves the stub as its prompt and two skills as resources; agent snapshots show the stub and both advertisements; the body snapshot is under its budget.
- [x] The home and music families cite the trigger claim and require the load; the mechanism scenarios that only choose (timer, schedule, six hours) pass with no load required, and the alarm-shape one requires it because its cited claim moved into the body; a scenario is shown red with body prose deleted and the description intact.
- [x] Every moved claim is cited or guarded as before; the commit notes each demonstrated red.
- [x] A full-tier pass after the move shows no claim rate below the backed-up scorecard; nabu's and jonas's standing token counts drop by the body; the ceiling's remaining figure is lowered.
- [x] Spec: `.scratch/prompt-skills/spec.md` § The setup index, § Rollout.

Result (2026-09-08): base 73/73, 223/228 runs (`.eval-output/scorecard-full.2026-09-08.after-load-tool-face.json`); after the move 73/73, 223/228 (`…after-home-skill-c.json`), one spurious load in 228. Two passes before it taught the stub: the snooze rule and the room-action rule are choosing rules and went back into the stub, and the description names `/timers` as not its business. Demonstrated red in `…home-body-deleted.json`: twelve of thirteen scenarios red with the description intact, every body claim 0/N, the load still made on every run but the snooze turn (which then chose a timer — the sentence that moved back). Nabu's standing prompt 13,358 → 10,567 tokens; ceiling 17,800 → 14,600.

