# Handoff — prompt skills, 2026-09-08 (evening)

Branch `skills`. Tickets 01–11 are all `Status: done`; each ticket file ends with a `Result` block naming
its before/after scorecards. Every full-tier scorecard of the day is kept as
`.eval-output/scorecard-full.2026-09-08.<step>.json` (the directory is gitignored; the two earliest
copies are also in `~/ziggurat-eval-backups/`).

## Where it landed

Seven skills, one per tool server, every one on the standard `skills://` shape through
`SkillServerResources`: `home-watches`, `home-assistant` (mcp-homeassistant), `obsidian-vault`
(mcp-vault), `web-browsing` (mcp-websearch), `scheduling` (mcp-scheduling, built per zone),
`sandbox` (mcp-sandbox, built from the mount at runtime through the new `AddSkill(name,
description, body factory)` overload), `countdown-timers` (mcp-timers). Each served section is now
a stub of choosing rules, plus whatever live text the server appends (the scheduling agent list, the
timer roster). `PromptManifest.StandingTokens` ratcheted 17,800 → 9,500. Nabu's standing prompt:
13,358 → ~6,100 tokens; the snapshot headers have the exact figures.

The load tool wears the repo's face (ticket 11): our description, `skillName` an enum of the
session's names. Spurious loads sat at 1–6 of 228 across the day's passes, all the probe reflex,
mostly `home-assistant` on timer turns.

## What the gates taught

- **A choosing rule that hides in a doing paragraph comes out red.** Ticket 05 took three passes:
  the snooze-after-a-dismissed-alarm rule and the room-action rule ("vacuum the study" is
  `clean_zone.sh`, not `start.sh`) both had to move back into the stub, and the home-assistant
  description had to name `/timers` as not its business before timer turns stopped loading it.
- **A stub sentence can teach the wrong shortcut.** Ticket 05's "the setup index names the entity,
  so do not load home-assistant for a watch" made watch turns skip the index read and copy the
  watches skill's example entity id into the home; it showed as two watch scenarios red in ticket
  06's gate, two passes running. The stub now puts the index read first, the example is an explicit
  placeholder (`sensor.<id from the setup index>`), and watch scenarios require the index read.
- **Most doing rules are the model's defaults.** Deleting a body with the description intact
  reddened 9 of 15 home claims, 5 of 15 vault, 2 of 10 web, 2 of 14 timers, and none of scheduling
  or sandbox (which declared none). Every claim that stayed green became a `Guard` with a dated
  note, as the repo's convention wants; the trigger claims and the demonstrated ones stay cited.
- **The eval's own vocabulary needed two loosenings:** command matchers accept a leading `./` (the
  mount does), and `CallPermission.Load(name)` tolerates one named skill's load on scenarios
  whose subject is elsewhere.
- **Recurring reds unrelated to any move** were the fixtures' own (fixed 2026-09-09, each 6/6
  armed after): the night-time watch asked for "below 55" beside a seeded "below 70", a tightening
  reading the model took a third of the time — it now asks for "above 250"; the delegated research
  reply is a bulleted forward of a seven-fact chronicle and the sentence counter counts bullets —
  the cap is 10, the judged synthesis check owns padding; the attachment vault turn had lost its
  one call of slack to the load — ceiling 7. What remains is the probe reflex: one to three
  `home-assistant` loads per pass on watch turns, measured rather than tuned.

## Review fixes (after the ten gates)

`/code-review` over `d7d369107..HEAD` found: three rule files stale against the split (fixed, with
the new skill files in their `paths:`); the snooze rule stated in both stub and body (the body now
keeps only "same summary and description"); the room-action rule undeclared (now
`home.room-request-uses-the-room-action`, cited by the vacuum scenario); the scheduling stub
declaring no claims (now two, guarded by the 2026-08-18 mechanism demonstration); two choosing
sentences duplicated into the timers body (trimmed); the sandbox description broader than its stub
(trimmed); wildcard load permissions (`CallPermission.Load(name)` now names the one skill a turn
passes through — a load's `skillName` reads as its path — and `CallExpectation.LoadsSkill(name)`
replaces five copies); the index registration duplicated in `AddSkill` (extracted). A final full
pass over those fixes: 73/73, 223/228 (`…after-review-fixes.json`), 3 spurious loads. Left as
judgement calls: the home stub at ~1,100 tokens keeps tool-level examples the gates justified; the
`^(\./)?` command matchers stay inline rather than behind an `Arg.Action` helper.

## Still open

- Body claims for `scheduling` and `sandbox` are undeclared, as their sections' were; the
  "declare in full" convention would want them written and exempted.
- The `home-watches` body claims `noisy-sensor-uses-for`, `spent-is-cleaned-up`,
  `crossing-only-is-said` are still `Unwritten` exemptions.
- The ceiling is 9,500 with 600 headroom; every section now standing is under the 500-token
  floor or read before a choice, so this rollout is complete unless the floor moves.
- Eval mechanics are unchanged from the morning: ~5 min per full pass, launch detached with
  `setsid nohup`, never rebuild while one runs, copy `scorecard-full.json` before any armed run.
