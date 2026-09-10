# home-watches — description 103 / 120 tokens, body 1671 / 2200 tokens, served by mcp-homeassistant

================================================================================================

---
name: home-watches
description: Writing, changing, pausing or removing a watch: a standing instruction the home runs when an entity's state meets a condition ("warn me when the sugar passes 180", "close the blinds when it gets hot", "tell me when the washer finishes"). Only for watches — not for switching or reading a device, an alarm, a reminder, a timer or music. The watch.json shape, trigger and effect kinds, delivery, once and enabled.
---

A watch is a file: `domain__filesystem__text_create` on `/ha/watches/<id>/watch.json` (`<id>` is a descriptive slug you
choose, as a schedule's is). The same `<id>` is the same watch, so a change replaces it in place.
The setup index you read this turn says which entities and which watches exist: a new watch is
written from the request, the entity id the index lists and this guide, without listing or
reading the other watches first. The example below is a shape, not a watch to copy — its
entity id and wording are invented, and the real ones come from the index and the request.

Every `entity_id` in a watch — trigger, condition or action target — is the bare id,
`sensor.temperatura_salon`, without the `_(friendly-name)` suffix the entity's directory
carries. That suffix belongs to paths you pass to a filesystem tool; a watch names the
entity to Home Assistant, which has never heard of it.

```json
{"name": "Greenhouse above 30",
 "triggers": [{"trigger": "numeric_state", "entity_id": "sensor.<id from the setup index>", "above": 30}],
 "conditions": [],
 "effects": [{"kind": "prompt", "prompt": "The greenhouse crossed 30 degrees. Read its history, say what it is and whether it is rising, and warn Fran."}],
 "once": false}
```

- `triggers` (required) and `conditions` (optional) are Home Assistant's own JSON, passed
  through as written, so any entity and any trigger the home understands can be watched.
  The usual shapes: `{"trigger": "numeric_state", "entity_id": "…", "above": 180}` (or
  `"below"`; on a noisy sensor add `"for": {"minutes": 5}` so one bad reading does not
  fire); `{"trigger": "state", "entity_id": "…", "from": "on", "to": "off"}`;
  `{"trigger": "template", "value_template": "{{ … }}"}`. A range ("leaves 70–180") is
  ONE watch with two triggers, one `below` and one `above` — never two watches. "Only at
  night" / "only when I'm home" are conditions: `{"condition": "time", "after":
  "23:00:00", "before": "07:00:00"}`, `{"condition": "state", "entity_id": "person.fran",
  "state": "home"}`.
- `numeric_state` fires on the **crossing** only, never at creation: a value already past
  the threshold warns at the next crossing, not now. Read `state.json` when you create the
  watch and, if it is already past, say the current value.
- `effects` (required, in order), each one of:
  - `{"kind": "prompt", "prompt": "…"}` — YOU run the prompt when it fires, as the agent
    that created the watch, and your answer goes to `deliverTo`. This is the default for a
    plain "warn me" / "avísame" / "tell me": you phrase the warning yourself when it fires
    (the value, the trend from `history.sh`). The fire brings you what fired — entity,
    from → to, when — even if the prompt says nothing about it, so "look into it" is enough.
  - `{"kind": "announce", "text": "…", "target": {…}, "insistent": {…}}` — a fixed
    sentence spoken in the home with no agent involved. Its whole shape is here; no other
    skill holds any of it. `target` is one of `{"room": "…"}`, `{"satelliteId": "…"}`,
    `{"satelliteIds": [ … ]}` or `{"all": true}`; `target.room` is a room from the setup
    index's `voice satellites:` line, spelled as it is there (or one of its satellite ids)
    — NEVER a Home Assistant area slug like `fran_s_office`; the write is refused naming
    the satellites that exist. `insistent` is always present and rings until acknowledged:
    an object with optional `gapSeconds`, `maxRepeats` and `maxDurationSeconds`, and `{}`
    takes every default. Add this effect only when the request carries urgency — "wake me
    if her sugar drops under 60" is an insistent announcement in the bedroom, usually
    followed by a `prompt` effect so the detail follows on chat. It works while the
    assistant is down.
  - `{"kind": "actions", "actions": [ … ]}` — Home Assistant actions the home performs
    itself, as written (`{"action": "cover.close_cover", "target": {"entity_id":
    "cover.…"}}`): no agent, and it keeps working while the assistant is down.
  Jinja is allowed in `prompt` and `text`: `{{ trigger.to_state.state }}` is the value that
  fired.
- `deliverTo` (prompt effects only; `channelId[:address]`, as for a schedule) is where your
  answer lands. Omit it and the answer goes where the request came from — the speaking
  satellite on voice, Telegram on Telegram, the chat on the chat — which is what a plain
  "warn me" wants; name it only to deliver somewhere else. A rewrite that omits it keeps
  the watch's delivery. Nabu has no Telegram, so on Nabu never write or promise Telegram
  delivery. `userId` (optional) is the person the fire runs as; omit it and it is the
  person who asked.
- `once: true` for "tell me when X finishes": the watch turns itself off after its first
  fire that reached you (a fire nobody was connected to take leaves it armed) and then
  reads `enabled: false` with `spent: true` in `status.json`. Remove spent
  watches (`remove`) when you list them or are asked about them. To arm a spent one again
  ("watch the next cycle too") edit it with `enabled: true` — a change to its threshold
  alone leaves it off.
- `enabled: false` pauses a watch ("stop warning me tonight") and `true` resumes it — an edit
  of the same file, never a delete.
- A watch is changed only when the user points at the one that exists ("warn me below 65,
  not 70", "move that alarm to the bedroom"): `text_edit` its `watch.json` — the same
  `<id>` is the same watch, replaced in place, so a change never leaves two watches (a full
  rewrite with `overwrite: true` replaces it too). Every other request on an entity that
  already has a watch is a NEW watch under its own id — a second threshold with its own
  effect, a night-only alarm next to a daytime warning, an insistent alarm where a plain
  warning exists — and the existing one keeps running as it was. A watch on the same
  entity is not a reason to edit it: never overwrite a watch the user did not ask to
  change. `domain__filesystem__remove` on `/ha/watches/<id>` removes it from the home.
- `status.json` beside it is read-only: `createdAt`, `lastTriggeredAt`, `automationEntity`,
  `spent`. To list watches, `domain__filesystem__glob` on `/ha/watches/*/` and read each `watch.json`.
- A watch needs no approval, even one that acts on the home: create it in the same turn.
  Then say it back to the user in one sentence — entity, direction, threshold, any `for`,
  what it does and where it delivers — from what you just wrote, so a misunderstanding is
  caught now; do not read the file again to do it. A write the home refuses comes back with
  Home Assistant's own message naming the field: fix that field and write again.
