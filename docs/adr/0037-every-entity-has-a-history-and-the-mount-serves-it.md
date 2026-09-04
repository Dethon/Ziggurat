# 0037 — Every entity has a history, and the mount serves it

Status: accepted
Date: 2026-09-04

## Context

The `/ha` mount gives the agent one value per entity: `state.json`, Home Assistant's current
stored state. That is the whole surface for the read-only classes — a sensor has no actions, so
its directory holds the one file — and it answers "what is it now" and nothing else.

A continuous glucose sensor made the gap concrete. The LibreLinkUp integration writes a reading a
minute; "how was it overnight", "is it climbing", "when did it last go low" are the questions worth
asking, and none of them has an answer in one number. The agent's only move was to read the file
again later, which is a pretend history that costs a turn per point.

Home Assistant keeps the answer. Its recorder stores every state change for a retention window
(ten days by default), and the history endpoint (`GET /api/history/period/{start}`) reads it back.
But like the calendar's uid, it is REST only: nothing in the service catalog reaches the recorder,
so the catalog-to-action-file rule the mount is built on cannot produce it.

## Decision

`history.sh` is an action served by the mount (`HaHistoryActions`, run by `HaHistory`), the
pattern ADR-0036 set for the calendar: an ordinary `HaServiceDefinition` injected through
`HaCatalogProvider(extraServices:)` so glob, `--help`, read and exec resolve it like any action,
with the exec call intercepted. One rule is new:

- **An action can declare itself for every entity.** `HaServiceDefinition.AppliesToEveryEntity`
  puts it in every entity directory under its bare name, read-only classes included. The resolver
  keeps its old rule for everything else — a cross-domain service earns a directory only by naming
  the class, which is what keeps `homeassistant.turn_on` out of every directory — and the setup
  index says such an action once, on an `every entity:` line, rather than on every class line.

The action reads the recorder through one new `IHomeAssistantClient` member, asked minimally
(state and instant per change, no attributes). The window crosses as the caller's strings, the
calendar's rule, and the date-time arithmetic both actions share moved to `HaDateTimeText`.
Defaults are the last 24 hours ending now; `--hours` counts back from the end, an explicit
`--start_date_time` excludes it.

Two shapes come back, because a minute-by-minute sensor over a day is 1,440 points and a light is
four. The plain listing keeps the latest `--limit` changes (200 by default) and says how many
there were; `--every <minutes>` answers min, max, mean, last and a sample count per clock-aligned
bucket, numeric states only, with the non-numeric ones (`unavailable`, `unknown`) counted as
skipped. A history with no number in it under `--every` is an argument error, not an empty answer.

## Considered options

- **A `history.json` file beside `state.json`.** A read takes no arguments, so the window and the
  bucket size would have to be fixed. A day of raw readings is too big to be a file the model reads
  on a whim, and any fixed summary is wrong for half the questions.
- **Long-term statistics.** Home Assistant keeps hourly mean/min/max forever for sensors with a
  `state_class`, which would answer "last month". They are WebSocket only
  (`recorder/statistics_during_period`), the glucose sensor declares no `state_class` so it has
  none, and the ten-day recorder covers the questions actually asked. Worth adding as a second
  action when a question needs more than the recorder keeps.
- **Only on classes with numeric states.** Every entity has a past; a door's history answers "when
  did it last open" as well as a sensor's answers a trend. Restricting it would need a rule about
  which classes count, and the rule would be wrong somewhere.
- **A separate MCP server for the glucose API.** LibreLinkUp's own API would give the same twelve
  hours the recorder does, through a second credential and a second integration to keep alive.
  Home Assistant already has the data, for every sensor, not one.

## Consequences

- The agent can answer a question about the past of anything in the home from one call, and the
  prompt teaches that the past is read from `history.sh`, never from `state.json` or repeated
  reads of it; the claim is declared and excused until the fake home keeps a past.
- The eval's fake home answers the history endpoint with an empty window, so the action is honest
  there rather than a 404; a scenario about history needs it to record changes first.
- What comes back is bounded by the recorder's retention. The empty answer names it, so a question
  about last month gets "nothing recorded in this window" and the reason, not a guess.
