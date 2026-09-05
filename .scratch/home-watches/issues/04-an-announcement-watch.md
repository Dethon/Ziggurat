# 04 — An announcement watch

**What to build:** Asked "wake me if Laura's sugar drops under 60 at night", the agent writes a watch whose effect is an `announce` with the alarm bridge's `target` and `insistent` shapes and a text that may carry a template for the value, plus a night-time condition; the home's automation calls the existing voice-announce rest_command with that payload, so it rings in the bedroom until acknowledged with no agent involved. The alarm bridge document and the local compose Home Assistant configuration currently disagree on the announce payload shape; the rendering follows what prod runs and the two are reconciled in this ticket. The eval's fake home gains a glucose sensor with a state class.

**Blocked by:** 03 — A home-action watch.

**Status:** done

- [x] Over the fake client: an `announce` effect renders the voice-announce call with text, target and insistent; a watch with an `announce` followed by `actions` renders them in that order.
- [x] The announce payload shape in the rendering, the bridge document and the local compose configuration agree; the integration seed carries the same rest_command.
- [x] The guide teaches when an announcement is the right effect (a fixed sentence, urgency, insistence) and that it needs no agent; claims declared.
- [x] Eval scenario: the night-time glucose alarm above (insistent announcement, condition), declaring the watch count as its change.
- [x] Spec: `.scratch/home-watches/spec.md` § Rendering the automation, § Provisioning and rollout.

## Comments

- 2026-09-05: the integration seed carries `assistant_watch_fired` alone — the command the
  end-to-end test drives. `voice_announce` is not seeded: nothing in the container tests rings a
  satellite, and a rest_command nothing calls would be dead configuration. The bridges document
  and the local compose config carry both.
- 2026-09-05, after the first prod try: the agent wrote the Home Assistant area slug as the announce
  room and the hub, which knows the room by its own name, dropped the fire. Announce targets are now
  resolved against the voice hub when the watch is written (the timers' rule, `ISatelliteCatalog`;
  `VoiceHub__BaseUrl` on the server) and the guide says a room is the hub's room, never an area slug.
