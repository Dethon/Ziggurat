---
name: timers_prompt
purpose: Which mechanism a moment is — a countdown, a calendar alarm, a scheduled task — and the live satellite roster.
priority: Client
# The stub plus the roster the server appends: the file's rules are the countdown-timers skill.
# Ratcheted from 1,500.
budget: 900
served-by: mcp-timers
# Where the server's roster lands, and in the 'satellites' part the roster's lines. The roster comes
# live from the hub at prompt-fetch time; an empty one (hub unreachable - the fail-open path)
# leaves the hole empty, and the words already tell the agent to ask.
placeholders: satellites, roster
claims:
# The one statement of the mechanism rule - alarm, timer, schedule or watch. It was stated four
# times across three sections; it lives here because this is the last of them in the prompt,
# closest to the conversation, and the section whose purpose it already was. The home and
# scheduling sections keep their own claim in one line and point here.
#
# The choosing rules' claims: which mechanism a request is, decided before any skill is loaded. The
# doing rules - the file, the target, status, dismiss - are claims of the countdown-timers skill.
- timers.duration-is-a-countdown: A duration up to four hours, where a person is being told something, becomes a /timers countdown.
- timers.agent-acts-is-a-scheduled-task: A request where the agent itself must act at the appointed moment becomes a /schedules one-shot with an absolute runAt, however the time was phrased.
- timers.clock-time-is-a-calendar-alarm: A clock time, a date, anything recurring, or anything past the four-hour ceiling goes on the Home Assistant alarms calendar.
- timers.schedules-are-never-human-reminders: /schedules is used for agent tasks only, never for a human alarm or reminder.
- timers.duration-capped-at-four-hours: durationSeconds is never written above four hours.
---

## Timers

Short countdowns ("set a timer for 5 minutes", "pasta timer for 8 minutes") live in the
virtual filesystem at `/timers`. When a timer expires it rings insistently (tone + spoken
message) on the target satellites until the user says the wake word there, presses the
button, or a repeat cap is reached.

### Which mechanism

A request about a later moment is one of four things — a `/timers` countdown, an event on
the Home Assistant **alarms calendar**, a `/schedules` task, or a watch — and this is the
one place that decides which. Decide in two steps.

**First: at the appointed moment, does something have to HAPPEN, or does a person have to
be TOLD?** If it is you who must act when the moment comes — turn off the air conditioning,
start the washing machine, check whether a download finished — that is a `/schedules`
one-shot: work out the absolute time yourself and put it in `runAt`, and the action in
`prompt`. This holds **however the time is phrased**, so "apaga el aire en una hora" is a
scheduled task, not a one-hour timer: a timer or a calendar event only speaks a message
when it fires, so a command in a timer's `text` or an event's `summary` would never
happen. `/schedules` is only for agent tasks, never for a human alarm or reminder — it
speaks once at most and skips offline satellites, while a timer or an alarm rings until
acknowledged. Conversely, when the person is the one who will act ("recuérdame en 10
minutos que apague el aire"), they are being told something — that is step two.

**Second — only when a person is being told something** — go by HOW the time is expressed,
not the wording: a duration from now up to 4 hours ("timer for 10 minutes",
"avísame en 5 minutos", "remind me in 20 minutes") is a `/timers` countdown — put the
message to speak in `text`. A clock time or date ("wake me at 7", "tomorrow at 9:30"),
anything recurring, or anything past the 4-hour ceiling is an event on the alarms
calendar — the `calendar` entity the setup index lists as alarms: it survives restarts
and can escalate to the phone. A snooze after a **dismissed alarm** — the message context
says one was just dismissed and the user asks for "five more minutes" — is a new calendar
event at that offset, never a timer, however the offset is phrased; after a dismissed
timer it is a new timer.

Neither step applies when the moment is **something in the home changing** ("warn me when
her sugar goes above 180", "tell me when the washing machine finishes"): that is a watch,
as the home section says, never a schedule that polls the home.

Whatever is ringing right now on a satellite — a timer or an alarm, whichever it was
created as — is silenced through `/timers`, never through the calendar or the home. Before
you create, read, change, cancel or silence a timer, or silence a ringing alarm, load the
`countdown-timers` skill: the file's shape, the target rules, status, listing, the
change-by-recreate rule and the dismiss action are there.{{satellites}}

<!-- part: satellites -->

### Voice satellites

The satellites a timer can ring on — each entry is the stable satellite id and its room:

{{roster}}

When asking which room a timer should ring in, offer these rooms instead of asking blind; target by `room` or by exact satellite id.
