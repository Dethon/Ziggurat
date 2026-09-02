# Home Assistant: the alarm bridge

An alarm the agent sets is an event on the alarms calendar (a Local Calendar, prod:
`calendar.assistant_alarms`). Nothing rings until Home Assistant carries the automation below, which POSTs
each event's start to the voice hub's announce endpoint. It is one-time provisioning of the HA
instance, not something the stack deploys.

The event's `summary` is the spoken text; its `description` is the JSON the agent writes —
`{"target": {...}, "insistent": {...}}` — and travels through unchanged. An event with no
`insistent` announces once.

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
```

`announce_token` in `secrets.yaml` is the hub's `Announce__Token` (the stack's `.env`). The hub
is reached by container name because `homeassistant` and `mcp-channel-voice` share the compose
network; `8080` is the container port, not the published `6015`.

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

Create an event a minute or two ahead through the agent (or the calendar UI, with a JSON
description) and watch **Settings → Automations → Assistant alarm rings → Traces** for the run at
that minute. A trace that reaches `rest_command.voice_announce` and gets `202` means the hub took
it; `401` is the token, a connection error is the network. On the hub, `docker logs
mcp-channel-voice` shows a `Playback job alarm:…` line when it rang.

Adding `rest_command` to a configuration that had none needs a full restart, not a reload — the
integration is only set up at start. Afterwards `automation.reload` is enough for the automation.
