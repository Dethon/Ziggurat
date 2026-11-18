# glucose-watches — description 127 / 130 tokens, body 1473 / 1600 tokens, served by mcp-sugary

================================================================================================

---
name: glucose-watches
description: Writing, changing, pausing or removing a watch on a person's glucose as Sugary reads their sensor: warned or woken when it goes under or over a level, falls or rises fast, is about to go low, or their glucose sensor stops sending readings ("warn me if my sugar drops under 70", "wake me in the bedroom if it goes under 60 at night", "tell me if my sensor stops"), their own or someone's under `/sugary/people/`. Never home-watches'. The watch.json shape, trigger kinds in mg/dL, effects, alarms, who sees it.
---

A glucose watch is a file: `domain__filesystem__text_create` on `/watchers/<id>/watch.json` (`<id>` is a
descriptive slug you choose). It sits beside the home's watches at `/watchers`; Sugary keeps it, because
its triggers are glucose kinds, and judges it on every reading of the person's sensor. The same `<id>` is
the same watch, so a change replaces it in place. The example is a shape, not a watch to copy.

```json
{"name": "Low under 70",
 "triggers": [{"trigger": "glucose", "below": 70, "for": {"minutes": 10}}],
 "effects": [{"kind": "prompt", "prompt": "My glucose went low. Say where it is and where it is heading."}],
 "once": false}
```

- Every level is **mg/dL**. A level asked in mmol/L is multiplied by 18.016 and rounded to a whole
  number — 3.9 is 70, 10 is 180 — and said back in the unit they asked in.
- `triggers` (required), each one of these four kinds — `glucose`, `glucose_trend`, `glucose_forecast`,
  `sensor` — spelled exactly so: a misspelled kind goes to the home and is refused in Home Assistant's
  words.
  - `{"trigger": "glucose", "below": 70}` (or `"above"`), with an optional `"for": {"minutes": 15}` to
    fire only once it has held that long. A range to leave ("outside 70–180") is one watch with two
    triggers.
  - `{"trigger": "glucose_trend", "falling": 2}` (or `"rising"`): mg/dL a minute over the last 15
    minutes. Falling fast, the sensor's double arrow, is 2; falling is 1.
  - `{"trigger": "glucose_forecast", "below": 70, "within": {"minutes": 20}}` (or `"above"`): the last
    20 minutes carried on would cross the level within that time — "before I go low".
  - `{"trigger": "sensor", "state": "no_readings", "for": {"minutes": 30}}`: Sugary reached the sensor's
    account and nothing new was measured for that long — "my sensor stopped", "it's not reading".
    `"state": "link_failing", "for": …` is Sugary unable to reach LibreLinkUp at all (refused, down,
    rate-limited) for that long. An outage never counts as `no_readings`, so a provider's hiccup never
    rings as a silent sensor; add `link_failing` only when they ask about the link. `"state":
    "ending", "within": {"hours": 24}` warns before the sensor's fourteen days run out.
  - `person`, on any trigger, names whose sensor: left out, the asker's own. Another person's glucose is watched only
    by someone who may read their log: their folder under `/sugary/people/` (`"person": "laura"`) on
    each trigger — never a glucose entity of the home. Refused there, say you cannot watch their glucose.
- `conditions` (optional) are only `{"condition": "time", "after": "23:00", "before": "07:00"}` and/or
  `"weekday": ["sat", "sun"]`, on the household's clock; `after` later than `before` spans midnight.
  "At night" is that.
- A trigger fires when its condition starts and not again until it has ended — never at creation.
  Before writing a level, read their newest reading with `domain__filesystem__query` on `/sugary` (on
  `/sugary/people/<name>` for theirs): `select read_at_local, mg_dl, trend from glucose order by
  read_at desc limit 1`. If it is already past the level, say that value and that the watch fires at
  the next crossing.
- `effects` (required, in order), each one of:
  - `{"kind": "prompt", "prompt": "…"}` — YOU run it when it fires, and your answer goes to
    `deliverTo`. The default for a plain "warn me". The fire brings what fired — the reading in mg/dL
    and mmol/L, its trend, the rate or forecast, when — so the prompt need not repeat it.
  - `{"kind": "announce", "text": "…", "target": {"room": "…"}, "insistent": {}}` — a fixed sentence,
    said as written, rung as an alarm with no agent involved. `target` is `{"room": "…"}`,
    `{"satelliteId": "…"}`, `{"satelliteIds": [ … ]}` or `{"all": true}`, a room or id from
    `/timers/satellites.json`. `insistent: {}` rings until it is stopped (the alarm's defaults, with
    their wake-up ramp); left out, it rings like a timer, about two minutes. Add it only when the
    request carries urgency — "wake me", "an alarm", "urgently" — in the room named, followed by a
    `prompt` effect so the detail follows in writing. With no satellites, or "on my phone", leave
    `target` out: it rings as an alarm on the person's chat and Telegram, as one does whose satellites
    are all offline when it fires.
  - There is no `actions` effect: a glucose watch acts on nothing in the home.
- A satellite's alarm is stopped on the satellite or with `./dismiss` in `/timers`; one ringing on chat
  and Telegram is stopped by its own Dismiss button there.
- `deliverTo` (prompt effects; `channelId[:address]`): omit it and the answer goes where the request
  came from. `userId`: omit it, it is the person asking.
- `once: true` for "only the next time": it turns itself off after its first fire that reached
  someone. `enabled: false` pauses ("not tonight") and `true` resumes — an edit, never a delete.
- A change the user points at edits that watch's `watch.json` (`text_edit`); any other request is a
  new watch under its own id. `domain__filesystem__remove` on `/watchers/<id>` removes it.
- `domain__filesystem__glob` on `/watchers/*/` lists the home's watches and the glucose watches
  together; read each `watch.json`. `status.json` says whose sensor it watches (`subject`), when it was
  made and last fired, `spent`, and whether each trigger holds now. A glucose watch is shown only to
  the person it watches, whoever wrote it, and whoever may read everyone's log. A listing that names
  something `unavailable` could not read it: say so, never that there are none.
- A watch needs no approval: write it in the same turn, then say it back in one sentence — whose
  glucose, the level in their unit (or the trend, or the sensor's state), any `for` or `within`, what
  it does and where — from what you wrote. A refused write names its field: fix it and write again.
