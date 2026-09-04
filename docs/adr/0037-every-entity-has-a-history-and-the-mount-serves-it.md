# 0037 — Every entity has a history, and the mount serves it (and its statistics)

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
skipped. A history with no number in it under `--every` is an argument error, not an empty answer,
and so is `--every` beside `--limit`.

The clock the buckets align to is the home's, not the stamps'. Home Assistant stamps every change
in UTC whatever zone the home keeps, so a day bucket cut on the stamps would open at UTC midnight —
02:00 in Madrid. The catalog therefore reads the home's zone once from `GET /api/config`, the one
place it is stated, and the summary opens a day at that zone's midnight, naming the zone in its
answer; a zone that cannot be read leaves the catalog whole and the buckets on UTC, said as much.

## Considered options

- **A `history.json` file beside `state.json`.** A read takes no arguments, so the window and the
  bucket size would have to be fixed. A day of raw readings is too big to be a file the model reads
  on a whim, and any fixed summary is wrong for half the questions.
- **Long-term statistics instead.** Home Assistant keeps hourly mean/min/max for good for sensors
  with a `state_class`, which answers "last month" but not "last night in detail", and the
  glucose sensor declared no `state_class`. They are the second read, below, not a replacement.
- **Only on classes with numeric states.** Every entity has a past; a door's history answers "when
  did it last open" as well as a sensor's answers a trend. Restricting it would need a rule about
  which classes count, and the rule would be wrong somewhere.
- **A separate MCP server for the glucose API.** LibreLinkUp's own API would give the same twelve
  hours the recorder does, through a second credential and a second integration to keep alive.
  Home Assistant already has the data, for every sensor, not one.

## The second read: statistics, for the entities that have them

The recorder's retention bounds `history.sh`, and raising it (prod went to 90 days the same day)
buys detail, not permanence. Home Assistant's long-term statistics are the permanent part: hourly
mean/min/max — or state/sum/change for a total such as energy — compiled at twelve past each hour
for every sensor with a `state_class`, kept outside the purge, and readable only by the WebSocket
command `recorder/statistics_during_period`.

So `statistics.sh` is a second served action (`HaStatisticsActions`, run by `HaStatistics`) with
one more rule:

- **An every-entity action can be narrowed by an attribute.** `RequiresAttribute = "state_class"`
  puts it in exactly the directories whose `state.json` carries that attribute, which is the same
  rule Home Assistant uses to decide whether to compile statistics at all — on the prod home the
  29 entities with a `state_class` are the 29 statistic ids the recorder lists. The resolver takes
  the entity rather than its id for this, and the setup index announces the action on its own
  line, `every entity with state_class:`.

The window is the history action's (`--days` in place of `--hours`, one resolver for both); a
`--period` of `5minute`, `hour`, `day`, `week` or `month` picks the row; the latest `--limit` rows
are kept. A row renders only the measures the sensor's kind has, so a total never shows a mean of
zero. The prompt divides the two: raw changes and the last few days from history, averages,
extremes and the long run from statistics.

A sensor an integration ships without a `state_class` gets one through HA's `customize:` — the
glucose sensor has since 2026-09-04 — and its first row lands within the hour.

## Consequences

- The agent can answer a question about the past of anything in the home from one call, and one
  about the long run of a measured sensor from another; the prompt teaches that the past is read
  from `history.sh` or `statistics.sh`, never from `state.json` or repeated reads of it, and the
  claim is declared and excused until the fake home keeps a past.
- The eval's fake home answers the history endpoint with an empty window and the statistics
  command from an empty map, so both actions are honest there rather than failing; a scenario
  about either needs the fake to keep a past first.
- What comes back is bounded by the recorder's retention. The empty answer names it, so a question
  about last month gets "nothing recorded in this window" and the reason, not a guess.
