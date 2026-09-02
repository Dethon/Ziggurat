# Home Assistant: the alarm bridge

An alarm the agent sets is an event on the alarms calendar (a Local Calendar, prod:
`calendar.alarms`). Nothing rings until Home Assistant carries the automation below, which POSTs
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

```yaml
- id: assistant_alarm_rings
  alias: Assistant alarm rings
  description: Bridges the alarms calendar to the voice hub's announce endpoint.
  mode: parallel
  triggers:
    - trigger: calendar
      entity_id: calendar.alarms
      event: start
  conditions:
    - condition: template
      value_template: >-
        {{ trigger.calendar_event.description is string
           and trigger.calendar_event.description.strip().startswith('{') }}
  actions:
    - variables:
        spec: "{{ trigger.calendar_event.description | from_json }}"
    - action: rest_command.voice_announce
      data:
        payload: >-
          {{ {
               'text': trigger.calendar_event.summary,
               'target': spec.target,
               'insistent': spec.get('insistent')
             } | to_json }}
```

`mode: parallel` lets two alarms at the same minute both ring. The condition skips an event whose
description is not JSON (a hand-made calendar entry) rather than failing the run.

## Checking it

Create an event a minute or two ahead through the agent (or the calendar UI, with a JSON
description) and watch **Settings → Automations → Assistant alarm rings → Traces**. A trace that
reaches `rest_command.voice_announce` and gets `202` means the hub took it; `401` is the token,
a connection error is the network.
