# Home Assistant: the bridges

Two things the agent leaves in the home come back into the stack through Home Assistant's own
`rest_command`s, provisioned once in the HA instance — never deployed by the stack:

- **The alarm bridge.** An alarm the agent sets is an event on the alarms calendar (a Local
  Calendar, prod: `calendar.assistant_alarms`). Nothing rings until the automation below POSTs each
  event's start to the voice hub's announce endpoint. The event's `summary` is the spoken text; its
  `description` is the JSON the agent writes — `{"target": {...}, "insistent": {...}}` — and travels
  through unchanged. An event with no `insistent` announces once.
- **The watch callback.** A watch (`/ha/watches/<id>/watch.json`, ADR 0038) is an automation the
  agent writes through the config API; nothing needs provisioning for its announcement and
  home-action effects — the announcement uses the same `voice_announce` command. A **prompt**
  effect is rendered as a call to `rest_command.assistant_watch_fired`, which POSTs the rendered
  prompt and the firing facts to the Home Assistant server's callback, and the agent that created
  the watch runs the prompt. Without the command a prompt watch is created and never reaches the
  agent, exactly as an alarm without the bridge never rings.

## configuration.yaml

```yaml
rest_command:
  voice_announce:
    url: "http://mcp-channel-voice:8080/api/voice/announce"
    method: POST
    timeout: 20
    headers:
      X-Announce-Token: !secret announce_token
      Content-Type: application/json
    payload: "{{ payload }}"
  assistant_watch_fired:
    url: "http://mcp-homeassistant:8080/api/homeassistant/watch-fired"
    method: POST
    timeout: 20
    headers:
      X-Announce-Token: !secret announce_token
      Content-Type: application/json
    payload: "{{ payload }}"
```

`announce_token` in `secrets.yaml` is the hub's `Announce__Token` (the stack's `.env`); the Home
Assistant server reads the same variable, so both commands share one secret. Both hosts are reached
by container name because `homeassistant`, `mcp-channel-voice` and `mcp-homeassistant` share the
compose network; `8080` is the container port, not the published one (`6015`, `6006`).

Both commands take the whole body as one `payload` string, composed on the HA side with
`to_json`. The rendering of a watch (`HaWatchAutomation.Render`) follows this shape exactly, and
the local compose configuration under `DockerCompose/volumes/homeassistant_config/` carries the
same two commands.

## automations.yaml

Provisioned on ai370 on 2026-09-02 and verified: an event created two minutes ahead rang on
the office satellite five seconds after its start.

```yaml
- id: assistant_alarm_rings
  alias: Assistant alarm rings
  description: Bridges the alarms calendar to the voice hub's announce endpoint. Polls once a
    minute rather than using the calendar trigger, which refetches only every 15 minutes and
    misses an alarm created inside that window.
  mode: single
  triggers:
  - trigger: time_pattern
    seconds: 5
  actions:
  - variables:
      window_start: "{{ now().replace(second=0, microsecond=0).isoformat() }}"
  - action: calendar.get_events
    target:
      entity_id: calendar.assistant_alarms
    data:
      start_date_time: "{{ window_start }}"
      duration:
        minutes: 1
    response_variable: found
  - repeat:
      for_each: "{{ found['calendar.assistant_alarms']['events'] }}"
      sequence:
      - if:
        - condition: template
          value_template: >-
            {{ as_datetime(repeat.item.start) >= as_datetime(window_start)
               and repeat.item.description is defined
               and repeat.item.description.strip().startswith('{') }}
        then:
        - variables:
            spec: "{{ repeat.item.description | from_json }}"
        - action: rest_command.voice_announce
          data:
            payload: >-
              {{ {'text': repeat.item.summary,
                  'target': spec.target,
                  'insistent': spec.get('insistent')} | to_json }}
```

Why a poll and not `trigger: calendar`: Home Assistant's calendar trigger fetches the upcoming
events once every fifteen minutes (`UPDATE_INTERVAL` in `calendar/trigger.py`) and has no hook on
the calendar changing, so an alarm created inside the current window — "remind me at half past",
said at twenty past — never fires. This was observed before switching: an event two minutes ahead
was ignored. The poll asks each minute for events starting in that minute; the start check keeps a
long event from ringing every minute it is running, and `mode: single` keeps a slow announce from
stacking runs. Latency is at most the five seconds into the minute.

## Checking it

**An alarm:** create an event a minute or two ahead through the agent (or the calendar UI, with a
JSON description) and watch **Settings → Automations → Assistant alarm rings → Traces** for the run
at that minute. A trace that reaches `rest_command.voice_announce` and gets `202` means the hub took
it; `401` is the token, a connection error is the network. On the hub, `docker logs
mcp-channel-voice` shows a `Playback job alarm:…` line when it rang.

**A watch:** ask the agent for one on an entity you can move ("avísame cuando encienda la luz de
la cocina"), then change the entity. The automation appears in **Settings → Automations** under
the watch's name, and its trace shows the fire: an `announce` effect reaching
`rest_command.voice_announce`, a `prompt` effect reaching `rest_command.assistant_watch_fired`. The
callback answers `202` when an agent took it (the answer then lands where the watch's `deliverTo`
says, in a conversation named after the watch), `503` when no agent is connected — the fire is
lost and the trace says so; nothing retries it — `401` for the token and `400` for a payload the
callback could not read. On the stack, `docker logs mcp-homeassistant` shows a `Watch … fired for
agent …` line per accepted fire. The automation's own on/off is the watch's `enabled`; a one-shot
(`once`) turns itself off after its first fire and reads as spent.

Adding a `rest_command` to a configuration needs a **full restart**, not a reload — the integration
is only set up at start; that holds for adding `assistant_watch_fired` beside an existing
`voice_announce`. Afterwards `automation.reload` is enough for the alarm automation, and a watch
reloads itself through the config API on every write.
