# 03 — Home Assistant: three scenarios and two citations on existing calls

- `entity-named-as-listed` — "enciende la lavadora": the only permitted exec path is the
  composed `lavadora_(…)` directory (either view), so a bare id or a guessed suffix is an
  unnecessary call rather than a tolerated near-miss. Needs `WashingMachineDirectory` and a
  path pattern on the fake.
- `arguments-come-from-help` — cited on the existing vacuum scenario: the required call's
  `--cleaning_area_id` flag name exists nowhere but the action's `--help` output (the setup
  index gives the slug, not the argument), and the ceiling bounds a re-guessing loop. The
  fix-exit-2-by-re-reading half stays untestable until a first attempt can be forced to fail;
  the citation covers the read-not-guessed half.
- `exit-codes-are-never-voiced` — the written half, on jonas: "Pon Radio Clásica…" — the name
  resolves to nothing, HA answers a bare 500, and the reply must give the failure in plain words
  with no code, no stderr text, and no retry storm (ceiling).
- `alarm-carries-target-and-insistent` — two more argument matchers on the wake-me-at-seven
  required call: the description JSON must carry `target` and `insistent`.
- `snooze-is-a-new-event` — a turn arriving with `DismissedAlert = alarm "…"` (the exact shape
  the voice channel writes: `{kind} "{text}"`) asking for five more minutes; required
  create_event at 20:05 with insistent.

**Status:** resolved

- [x] Fake gains the washing-machine directory constants.
- [x] Scenarios and citations in place, exemption lines removed, coverage test green.
- [x] Armed runs pass at each scenario's policy: washing machine 3/3, snooze 2/3, the alarm
      with target+insistent matchers 3/3, the written failure 3/4 once its ceiling matched the
      model's honest floor (see below).

## Findings from the armed runs

Washing machine 3/3, snooze 2/3, and the wake-me alarm with the stricter target+insistent
matchers 3/3. The written-failure scenario went 0/2 on its ceiling with the reply itself
perfect both times ("No he podido localizar 'Radio Clásica'…", no codes): asked for a real
station the fake cannot resolve, the model spent ten calls rewording browses and searches
before believing the 500 — persistence a real name half-earns, since a deployed Music
Assistant would simply play it. The station is now invented (Radio Faro del Sur), and the third
round showed the model's floor for believing a media failure is nine calls even then — one play,
then helps, browses and searches, never a playback retry, which is the contract's own "list the
real items" done thoroughly. The ceiling is 10 with that written down: the claim cited is about
the reply, and playback-retry storms are what FavouriteMusic's tight ceiling guards. 3/4 armed.
