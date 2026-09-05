# 0036 — The calendar's action files are served by the mount

Status: accepted
Date: 2026-09-02

## Context

The `/ha` mount turns Home Assistant's service catalog into action files: every service that
targets an entity class becomes `<service>.sh` in that class's directories, with `--help` rendered
from the service's fields. It is a good rule because nothing is declared twice — what a home
publishes is what the agent can run.

The calendar is the one class the rule fails on. `calendar.get_events` exists, but its response is
filtered to start, end, summary, description, location and status: the `uid` an event is addressed
by is not in it. `calendar.create_event` exists, but takes no recurrence rule. And deleting an event
has no service at all — Home Assistant offers it, with update, only as WebSocket commands
(`calendar/event/delete`, `calendar/event/update`, `calendar/event/create`), and the uid only on the
REST calendars endpoint (`GET /api/calendars/{entity}`). The prompt promised `delete_event.sh` and
`update_event.sh`, and the eval's fake catalog published both, so the eval passed against a surface
no deployment had.

A real turn found the gap. Asked to move a reminder, the agent ran `update_event.sh --help`, was
told `command not found`, created a second event at the new time and reported success. The old
one stayed, and there was no call that could remove it.

## Decision

The calendar's three actions — `create_event`, `get_events`, `delete_event` — are declared by the
mount (`HaCalendarActions`) and served by it (`HaCalendarEvents`), the pattern the podcast listing
set: ordinary `HaServiceDefinition`s injected through `HaCatalogProvider(extraServices:)` so glob,
`--help` and exec resolve them like any other action, with the exec call intercepted. Two rules are
new:

- **A served action with a catalog service's name replaces it.** The catalog keeps every other
  service; `create_event` and `get_events` are the served ones, so a calendar directory lists each
  action once and the definition the model reads is the one that runs.
- **There is no update.** A change to an alarm is a delete by uid and a fresh create. Update would
  need the whole event re-sent (the WebSocket schema requires summary, start and end), which means
  a read first, and the timers mount already teaches delete-and-recreate as the way to change one.

They ride three new `IHomeAssistantClient` members: the REST listing that carries uids, and create
and delete over one WebSocket connection per command. Date-times cross as the caller's strings —
Home Assistant reads a naive one in its own time zone, and the container may sit in another.

## Considered options

- **Ask Home Assistant for a delete service.** It would be the honest fix to the rule, and it is not
  ours to make; the WebSocket command is the documented way.
- **A `calendar.delete_event` script in the HA instance.** Scripts can only call services, and the
  service does not exist.
- **Keep `create_event` on the REST service and add only delete.** One action per transport is
  simpler to explain, and it leaves recurring alarms impossible — the prompt promised `--rrule` and
  the argument parser rejected it. Serving create as well makes the rule true.
- **Update in place.** See above; it is also the action the transcript reached for and would have
  been wrong to satisfy, because "move it to 10:30" then needs the agent to re-supply the message
  and the target it did not change.

## Consequences

- An alarm can be cancelled, and moved, from voice. The prompt teaches list-then-delete-by-uid and
  names the claim; an eval scenario cancels a seeded alarm and declares the calendar's event count
  as the change.
- The eval fake home publishes the calendar domain Home Assistant really has and answers the
  calendars endpoint and the WebSocket commands from one store, so the fake can no longer be more
  capable than the deployment. `FakeHomeAssistantSocket` is a real Kestrel server, the way the
  Music Assistant fake is, because a message handler cannot answer a socket upgrade.
- The integration seed boots the real container with a Local Calendar config entry, so the
  round trip is proven against the component, recurrence expansion included.
- Nothing rings unless the HA instance carries the bridge automation. That is provisioning, not
  code; `docs/home-assistant-bridges.md` holds it.
