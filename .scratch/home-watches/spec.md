# Home watches

Status: ready-for-agent

Grilled 2026-09-05. Glossary: `CONTEXT.md` § Home watches (**Watch**, **Effect**). Decision:
`docs/adr/0038-a-watch-is-a-home-automation-and-the-home-carries-its-fires-back.md`.

## Problem Statement

Francisco can ask Jonas or Nabu what the home is doing right now, what it did over the last
days, and to do something now or at a clock time. He cannot ask either of them to react to the
home changing. "Warn me when Laura's sugar passes 180", "turn the heating down when the study
window opens", "tell me when the washing machine finishes" all end with the agent explaining that
it has no way to watch a sensor. The only reactive thing in the stack, the alarm calendar, reacts
to the clock.

Laura's glucose is the case that matters most: a threshold crossing at night has to reach
somebody, and a schedule that polls history every few minutes is late, noisy, and spends a model
turn every tick.

## Solution

The agent can leave a **watch** on the home: when an entity meets a condition, the watch's
effects happen. An effect is a prompt the creating agent runs, an announcement spoken in a room
(insistent if asked), or a home action performed with no agent at all; a watch lists one or more,
in order.

A watch is a real Home Assistant automation. The agent writes it as a `watch.json` file under a
new `/ha/watches/` subtree of the ha mount, the mount renders the file into an automation through
Home Assistant's automation config API, and Home Assistant evaluates and runs it from then on. It
shows in Home Assistant's UI with a trace per fire, and it keeps working for announcements and
home actions while the stack is down. A prompt effect travels back through a hand-provisioned
`rest_command`, the way the alarm calendar's announcements already do, into a callback endpoint on
the Home Assistant server, which raises the prompt with the agent that created the watch and
delivers the answer where the watch says.

Triggers, conditions and home actions are written in Home Assistant's own JSON, so any entity and
any trigger the home understands can be watched. Both agents see every watch; the agent that
created a watch is the one that runs its prompts.

## User Stories

1. As a person talking to Jonas on Telegram, I want to say "warn me when Laura's sugar goes above 180", so that I am told without asking.
2. As a person talking to Nabu in the office, I want the same request to be spoken to me in the office when it fires, so that a voice request answers by voice.
3. As a person, I want "warn me when Laura's sugar leaves 70 to 180" to become one watch with two triggers, so that a range is one thing to list and remove.
4. As a person, I want the warning to be phrased by the agent (what the value is, whether it is rising, what history shows), so that a threshold crossing comes with judgement, not just a number.
5. As a person, I want to say "wake me if her sugar drops under 60 at night" and get an insistent announcement in my bedroom that rings until I acknowledge it, so that a dangerous reading is never missed while I sleep.
6. As a person, I want a watch that both rings insistently in a room and sends the agent's reasoned message to Telegram, so that an urgent fire reaches me now and the detail follows.
7. As a person, I want to say "close the blinds when the living-room temperature goes above 27", so that the home acts with nobody in the loop.
8. As a person, I want a home-action watch to keep working while the agent stack is restarting or down, so that the house does not depend on the assistant being up.
9. As a person, I want to say "tell me when the washing machine finishes" and have it fire once, so that I am not told every cycle for the rest of my life.
10. As a person, I want to say "only at night" or "only when I am home" and have that honoured, so that a watch does not fire when it does not matter.
11. As a person, I want to watch any entity the home has — a glucose sensor, a door contact, a vacuum, a media player, a person's presence — so that the feature is not tied to one integration.
12. As a person, I want to ask "what watches do I have?" and hear each one's name, what it watches, what it does and whether it is on, so that I know what the home is doing on my behalf.
13. As a person, I want to ask Nabu about a watch Jonas created and vice versa, so that the home has one list, not one per agent.
14. As a person, I want to say "stop warning me about her sugar for tonight" and have the watch paused rather than deleted, so that resuming is one sentence.
15. As a person, I want to say "change the threshold to 200" and have the same watch updated, so that I do not end up with two watches like I once did with two alarms.
16. As a person, I want to say "remove the sugar watch", so that it is gone from the home as well as the list.
17. As a person, I want the agent to read back what it created — entity, direction, threshold, any duration, the effects, where it delivers — so that I can catch a misunderstanding immediately.
18. As a person, I want a watch on a noisy sensor to be able to require the condition to hold for a while before firing, so that a single bad reading does not wake me.
19. As a person, I want a spent one-shot watch to be visibly spent and cleaned up when I next list or ask, so that the list does not fill with dead entries.
20. As a person, I want the spoken or prompted text to be able to include the value that fired ("her sugar is 183"), so that the fire says what happened.
21. As a person, I want the agent to know what fired even when the watch's prompt says nothing about it, so that "look into it" is enough of a prompt.
22. As a person, I want the answer to a fired prompt watch to land in a conversation I can open later, so that a warning sent while I slept is still there in the morning.
23. As a person, I want a watch that would act on the home to be created without an approval round trip, so that setting one up by voice is one exchange.
24. As the operator, I want a watch to be visible in Home Assistant's own UI with a trace per fire, so that I can see why it did or did not fire without reading container logs.
25. As the operator, I want a prompt watch that fired while the stack had no agent to show a failed call in the automation's trace, so that a lost fire is visible rather than silent.
26. As the operator, I want a brief agent reconnect not to lose a fire, so that a container restart during a crossing still warns me.
27. As the operator, I want the callback into the stack guarded by the token the announce bridge already uses, so that provisioning is one secret in Home Assistant.
28. As the operator, I want the provisioning documented beside the alarm bridge, with the full-restart caveat, so that prod can be set up in one sitting.
29. As the operator, I want hand-made automations (the alarm bridge, blueprints) to stay out of the watch list, so that the agent never edits or removes one.
30. As the agent, I want the ha mount's index to tell me watches exist and where, so that I discover the feature without being told.
31. As the agent, I want a bad trigger or action to be refused at write time with Home Assistant's own message, so that I can fix it in the same turn instead of creating a watch that never fires.
32. As the agent, I want the same `deliverTo` and `userId` fields I already know from schedules, so that one idiom serves both.
33. As the agent on Nabu, I want to be told I cannot deliver a prompt watch to Telegram, so that I do not promise a channel I do not have.
34. As the eval author, I want scenarios per effect kind that assert the automation the fake home received, so that the prompt idiom is proven against a real model.
35. As the integration test author, I want the seeded container to carry the rest_command, so that one test proves a state change fires a watch and reaches the callback.

## Implementation Decisions

### The watch record

- A watch is a Home Assistant automation whose id carries an assistant prefix and whose
  description holds the watch's metadata as JSON (the alarm calendar's description idiom). The
  metadata carries what the automation cannot express by itself: the creating agent, the effects
  as authored, `once`, `deliverTo`, `userId`, and the creation instant. Only automations with the
  prefix and a parsable description are watches; everything else is invisible to `/ha/watches`
  and untouched by it, while still appearing under `/ha/entities/automation/` as an entity.
- The mount renders `watch.json` into the automation on every write and projects the automation
  back into `watch.json` on every read, so the file is never stored anywhere but in the home.

### The mount

- `/ha/watches/` is the ha mount's first writable subtree. The rest of the mount stays
  read-and-exec. `HaVfsPath` gains the watch kinds (watches root, watch directory, watch file,
  watch status file); `HaTree` lists them; the filesystem's create, write, delete and glob answer
  for them and keep refusing everywhere else.
- `/ha/watches/<id>/watch.json` is the authored file. `<id>` is chosen by the agent as a
  descriptive slug (as a schedule id is); the automation id is the prefix plus that slug.
- `watch.json` fields:
  - `name` (required) — the human name; becomes the automation's alias.
  - `triggers` (required) — a non-empty list of Home Assistant triggers, passed through as
    written.
  - `conditions` (optional) — a list of Home Assistant conditions, passed through as written.
  - `effects` (required) — a non-empty ordered list; each is one of:
    - `{"kind": "prompt", "prompt": "<text, Jinja allowed>"}`
    - `{"kind": "announce", "text": "<text, Jinja allowed>", "target": {...}, "insistent": {...}?}`
      — `target` and `insistent` are the alarm bridge's shapes, unchanged.
    - `{"kind": "actions", "actions": [ ...Home Assistant actions as written... ]}`
  - `once` (optional, default false) — the watch turns itself off after its first fire.
  - `enabled` (optional, default true) — write false to pause, true to resume. On read it
    reflects the automation's on/off state, so a spent one-shot reads false.
  - `deliverTo` (optional) and `userId` (optional) — exactly the schedule's fields and meaning;
    they apply to prompt effects only. The default `deliverTo` is the same configured default
    schedules use.
- `/ha/watches/<id>/status.json` is read-only: `createdAt`, `lastTriggeredAt` (the automation
  entity's `last_triggered`), `automationEntity` (the entity id), `spent` (true when `once` and
  the automation is off).
- Deleting the directory (or the file) deletes the automation. Writing an existing `watch.json`
  replaces the automation in place under the same id, so a change never leaves a second watch.
- Validation at write time: a file that is not the shape above is a write error naming the
  field; a trigger, condition or action Home Assistant rejects is a write error carrying Home
  Assistant's own message; an unknown effect kind, an empty `effects`, an empty `triggers` are
  errors before anything reaches the home.

### Rendering the automation

- `triggers` and `conditions` are the automation's `triggers` and `conditions` verbatim.
- `mode: single`, so a slow prompt callback cannot stack runs.
- Effects render in order into the automation's action sequence:
  - `announce` → the existing `rest_command.voice_announce` call with `text`, `target` and
    `insistent`, in whatever payload shape the deployment's rest_command expects (the doc and the
    local compose config currently differ in shape; the rendering follows the documented bridge
    and the doc is corrected if the two disagree).
  - `actions` → the actions verbatim.
  - `prompt` → a call to a new `rest_command.assistant_watch_fired` with a JSON payload: the
    watch id, the watch name, the rendered prompt, and the firing facts Home Assistant knows at
    fire time — entity id, friendly name, from-state, to-state, the trigger's own description,
    and the fire instant — all rendered from `trigger.*` template variables.
- A `once` watch appends a final `automation.turn_off` targeting its own entity.
- Text fields cross to Home Assistant as templates; nothing is rendered on our side.

### The callback

- `McpServerHomeAssistant` becomes a dual-role server: it keeps its tool server registration
  and adds a channel server registration with the **Broadcast** policy and no outbound surface
  (like scheduling, it never carries a reply). The channel id is registered in the agent's
  channel endpoints beside `scheduling`.
- A new HTTP endpoint on the server accepts the `rest_command`'s POST, guarded by the same header
  and token scheme as the voice hub's announce endpoint and configured from the same secret
  (`Announce__Token`), which the compose file wires into this container too. Wrong or missing
  token → 401. Malformed payload → 400.
- The endpoint composes one channel message notification: conversation id derived from the
  watch id and the fire instant (as schedules do), sender `watch`, agent id the watch's creator,
  content = a first line stating what fired (name, entity, from → to, when) followed by the
  rendered prompt, reply targets from the watch's `deliverTo` parsed and coalesced exactly as the
  schedule fire planner does, and an origin of a new **Watch** kind carrying the watch id. The
  minted conversation is titled after the watch's name.
- Emit by broadcast. `true` → 202. `false` (no subscriber registered at all) → 503 with a body
  saying no agent is connected, so the Home Assistant trace shows the lost fire. Nothing is
  retried or buffered by us.
- The watch's `userId` is put on the notification the same way scheduling would if it did; if
  scheduling does not carry it today, the shared fire-planning code is extended so both do.

### Home Assistant client

- `IHomeAssistantClient` gains the automation config operations: list automations (with their
  entity state so on/off and `last_triggered` come along), get one config by id, upsert one
  config by id, delete one by id. Upsert surfaces Home Assistant's validation failure as a typed
  error carrying HA's message, not a bare status code.
- The config API is present because the deployment loads `default_config`; a home without the
  `config` integration makes every watch write fail with that reason, and the setup index says
  watches are unavailable rather than listing an empty subtree.

### Prompts and discovery

- The `home_assistant_guide` prompt gains a watches section: what a watch is, the file shape, the
  three effect kinds, the common trigger shapes (`numeric_state` above/below with crossing
  semantics and optional `for`, `state` to/from, `template`), a range as two triggers, conditions
  for "only at night", `once` for "tell me when", `enabled` for pausing, delete-and-rewrite never
  needed because writes replace in place, and read-back after creation. It states the default:
  a plain "warn me" is a prompt watch delivered to the channel it was asked from — on voice the
  speaking satellite, on Telegram Telegram — and an insistent announcement is added only when
  asked for urgency. It says Nabu has no Telegram delivery.
- The prompt names the boundary with the other reactive tools: clock → alarm calendar or
  timers; deferred action → schedules; home changes → watches.
- Each rule that a scenario can check is declared as a claim beside its prose (ADR 0031).
- The setup index lists `watches:` with the subtree and the count of existing watches.

### Provisioning and rollout

- The alarm bridge document becomes the "Home Assistant bridges" document with a second
  `rest_command.assistant_watch_fired` pointing at the new endpoint by container name, sharing
  `announce_token`. It repeats the caveat: adding a rest_command needs a full Home Assistant
  restart; automations reload themselves through the config API.
- The compose file wires the token into `mcp-homeassistant`; `.env` gets no new placeholder
  because the secret already exists.
- The local compose Home Assistant config and the integration seed carry the new rest_command.
- The ADR's status moves from proposed to accepted when the feature ships.

## Testing Decisions

A good test drives the seam an outside party drives and asserts what that party sees: the files
the agent reads and the errors it gets, the automation Home Assistant received, the notification
that landed in the inbox, the HTTP status the home was answered with. Nothing asserts on
rendering helpers, path parsers or internal records.

Seams, all agreed:

1. **`HaFileSystem` over `FakeHaClient`** (existing unit seam; prior art: the calendar action and
   statistics action tests). `FakeHaClient` gains an automation store. Tests cover: the watches
   root and the index line; create, read-back, edit in place, pause/resume, delete; the rendered
   automation for each effect kind and for combinations, `once` rendering the self turn-off,
   `mode: single`; `status.json` fields including spent; every write error; a hand-made automation
   never appearing; the mount still refusing writes outside the subtree.
2. **The callback endpoint** (new seam at the HTTP boundary; prior art: the announce and dismiss
   endpoint auth tests, and the scheduling dispatcher tests that drain a real `ChannelInbox`).
   Tests cover: 401, 400, 202 with the composed notification (content's first line, agent id,
   reply targets, origin, conversation id), 503 with no subscriber, and delivery to a stale but
   registered subscriber.
3. **`HomeAssistantClient` against the real container** (existing; prior art: the calendar and
   statistics client tests over `HomeAssistantSeed`). Tests cover the upsert/get/list/delete round
   trip, HA's validation message on a bad trigger, and one full fire: the seed provisions the
   rest_command towards a test listener, the test writes a watch, sets the entity's state through
   the REST states endpoint across the threshold, and the listener receives the payload with the
   firing facts (prior art: the timer ring end-to-end test).
4. **Eval** (existing; prior art: the alarm scenarios and their event-count change). The fake
   home serves the automation config API from a store, counts watches in its snapshot, and gains
   a glucose sensor with a `state_class`. Scenarios: a plain warn on Telegram (prompt effect,
   deliverTo telegram), the same on voice (deliverTo the speaking satellite), a range (two
   triggers), an urgent one (insistent announcement plus prompt), a home action, a one-shot,
   pausing, a threshold change (same watch id, count unchanged), and removal. Claims sit beside
   the prompt prose and the claim-coverage test keeps every one covered.
5. **Prompt snapshots and setup summary** (existing unit tests) pin the new section and the index
   line.

## Out of Scope

- Watches on anything but Home Assistant entities (calendar time, web pages, downloads).
- Retrying or buffering a fire on our side; a fire lost while no agent is subscribed is visible
  in the trace and nothing else.
- Editing or listing hand-made Home Assistant automations through the mount.
- An update service for schedules or alarms; only watches replace in place.
- A stack-side evaluator (persistent WebSocket, `subscribe_trigger`); rejected in ADR 0038.
- Telegram delivery for Nabu; that is an agent catalogue question, not a watch one.
- Self-provisioning the rest_command from the stack; Home Assistant offers no API for YAML config.
- Deleting a spent one-shot automatically; it is turned off and cleaned up by the agent.

## Further Notes

- Home Assistant's `numeric_state` trigger fires on the crossing only, and does not fire at
  subscription if the value is already past the threshold; the prompt should say so, since "warn
  me when it goes above 180" while it is already 190 warns at the next crossing, not now. The
  agent can read `state.json` and say the current value when it creates the watch.
- The alarm bridge doc and the local compose config disagree on the `voice_announce` payload
  shape; the rendering must match the deployed one and the two must be reconciled in this change.
- `mode: single` means a watch whose prompt callback is slow drops fires that arrive during the
  call; the call is a single POST answered in milliseconds, so this is not expected to bite.
- Memory: `home-watches-grilled-not-specced` records the settled decisions and must be updated
  to say the spec exists.
