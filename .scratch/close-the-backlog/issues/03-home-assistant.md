# Home Assistant: the five unwritten claims

Status: resolved

## Answer

Armed on 2026-08-19, all five at or above threshold: entity-named 3/3, arguments-from-help
3/3 (the fan-speed enum fixture proven by two new FakeHomeAssistantTests), written-failure
3/3, alarm-shape 2/3, snooze 2/3.

- `home.entity-named-as-listed` — "pon la lavadora": exec is permitted only against the exact
  composed directory `lavadora_(lavadora)`, so a bare id or guessed suffix fails as an
  unnecessary call. The old exemption wanted a ceiling-tight retry scenario; pinning the
  permitted surface to the composed name is the same rule with a deterministic edge.
- `home.arguments-come-from-help` — new fixture surface: `vacuum.set_fan_speed` takes a select
  whose options are `Silencioso|Normal|Turbo`. The user says "modo turbo"; the parser rejects
  anything but the listed casing byte-for-byte (options printed by `--help` and by the
  rejection), and the state diff demands `#fan_speed = Turbo`. A guessed argument costs a
  rejected call; repeating the same shape breaks the ceiling. Fixture proven by two new
  `FakeHomeAssistantTests`.
- `home.exit-codes-are-never-voiced` — the written half: "Apaga la luz del garaje." on the
  written channel; plain words required, codes and entity ids forbidden.
- `home.alarm-carries-target-and-insistent` — `WakeMeAtSeven`'s required call now also matches
  `target` and `insistent` in the command, and the scenario cites this claim (it had none).
- `home.snooze-is-a-new-event` — "cinco minutos más" with `DismissedAlert = "alarma de la
  medicación"`: a create_event at 20:05 carrying the summary and insistent, with /timers
  hosted and unpermitted.
