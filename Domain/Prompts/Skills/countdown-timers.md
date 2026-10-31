---
name: countdown-timers
description: Stopping an alarm or timer that is ringing right now ("stop the alarm", "para la alarma"), and touching a countdown under `/timers`: setting one ("timer for eight minutes", "remind me in twenty"), how long is left, listing, cancelling or adding time to one. Not for a calendar alarm named by what it is for — removing, moving or snoozing "the dentist alarm" is the home's — nor a watch, a scheduled task or moving a player. The timer.json shape, target rules, status.json, change by recreate, ./dismiss.
description-budget: 130
body-budget: 900
served-by: mcp-timers
claims:
# The trigger claim: a request of this kind loads the skill. Cited by every timer scenario, so a
# skill nobody loads shows as a red description rather than a red body.
- countdown-timers.loads-for-a-timer-request: A request to set, read, list, cancel, change or silence a countdown loads the countdown-timers skill before /timers is touched.
- countdown-timers.created-at-its-own-path: A countdown is created as JSON at /timers/<descriptive-id>/timer.json carrying durationSeconds and an optional spoken text.
- countdown-timers.id-is-descriptive: The timer's id describes what it is for, because a timer with no text announces itself by its id.
- countdown-timers.voice-targets-the-speaking-room: On a voice turn the timer targets the room the request came from, unless another room is named.
- countdown-timers.no-satellite-asks-which-room: On a turn with no speaking room the agent asks which room or satellite before creating anything, and never guesses one.
- countdown-timers.text-is-spoken-never-an-instruction: The text of a timer is a message spoken to a person, never a command to be carried out.
- countdown-timers.status-is-read-for-time-left: How long is left is read from /timers/<id>/status.json rather than calculated.
- countdown-timers.spoken-status-gives-only-the-remaining-time: A spoken reply about a running timer gives the remaining time alone, without the firing time.
- countdown-timers.written-status-includes-fires-at: A written reply includes firesAt when the user asked when the timer fires.
- countdown-timers.listed-by-glob: The timers that exist are listed by globbing /timers.
- countdown-timers.cancelled-by-removing-it: A timer is cancelled by removing /timers/<id>.
- countdown-timers.changed-by-delete-and-recreate: A running timer is changed by reading its status, deleting it, and creating its replacement, in that order.
- countdown-timers.extending-a-dismissed-one-is-a-new-timer: Extending a timer that has already fired and been dismissed creates a new timer rather than reviving the old one.
- countdown-timers.recreation-is-never-narrated: The delete-and-recreate is internal: the reply states the new time and never mentions deleting or recreating.
- countdown-timers.ringing-is-stopped-by-dismiss: A request to stop or dismiss a ringing timer runs ./dismiss at /timers, from any room and any channel.
---

- Create: `domain__filesystem__text_create` at `/timers/<descriptive-id>/timer.json` with JSON
  `{"durationSeconds": <int>, "text"?: "<spoken message>", "target": {...} }`.
  `target` is `{satelliteId | satelliteIds | room | all}`. On a voice turn, default to the
  **speaking room** (the room this request came from) unless another room is named. On any
  other channel there is no speaking room, and **nothing else supplies one**: not what the
  timer is for (a pasta timer does not imply the kitchen), not anything remembered about
  the user, not a room used before. Ask which room or satellite it should ring on before
  creating the timer, and never guess (a timer rings only on its target satellites, so a
  wrong or absent one either rings in an empty room or fails to arm). When `text` is
  omitted the timer announces itself as "<id> timer", so pick a descriptive id (e.g. `pasta`).
  `text` is spoken to a person and is **never an instruction** to be carried out.
- Time left: `domain__filesystem__file_read` on `/timers/<id>/status.json` → `remainingSeconds`
  and `firesAt`. When your reply is spoken, give only the remaining time; in a written reply
  include `firesAt` if the user asked when it fires.
- List: `domain__filesystem__glob` on `/timers`.
- Cancel: `domain__filesystem__remove` on `/timers/<id>`.
- Timers are immutable and fire once — to change one, delete it and create a new one; to
  extend one just dismissed ("two more minutes"), create a new timer. This is internal —
  never mention deleting or recreating, just state the new time.
- To change a **running** timer ("add five minutes to the pasta timer"): read its
  `status.json` for `remainingSeconds`, delete the timer, and recreate it with the
  adjusted remainder.
- Stop ringing: when the user asks to stop or dismiss a ringing alarm/timer (from any room
  or any channel), `domain__filesystem__exec` `./dismiss` at `/timers` — it silences everything
  currently ringing on all satellites. A fired timer no longer appears under `/timers`;
  `./dismiss` is the only way to silence it remotely. That one call is the whole task:
  answer once it returns, and do not go looking for whatever set the alert off — not the
  alarms calendar, not the entity, not the setup index. What was ringing does not matter;
  it has stopped.
- Inside a sandbox command, `/timers` is a directory and `/timers/dismiss` runs from any
  script with its own output and exit code — for a request that dismisses as one step of
  a script. On its own, the one exec above is still the whole task.
