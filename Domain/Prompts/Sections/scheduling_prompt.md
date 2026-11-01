---
name: scheduling_prompt
purpose: That a schedule is a deferred action of the agent's own, never a human reminder, and that writing one loads the skill.
priority: Client
# The stub alone: the file's rules are the scheduling skill. It names no agent — it is fetched for
# nobody and cached for every household, so an agent list here was the home's shown to everybody;
# a household's own agents are the directories its /schedules lists.
# Ratcheted from 2,000, then from 700 when the mechanism rule moved to its one statement under
# Timers.
budget: 500
served-by: mcp-scheduling
claims:
# The choosing rules' claims: what is decided before the scheduling skill is loaded. The file's
# rules are the skill's.
- scheduling.never-a-human-reminder: A human alarm, wake-up or reminder is never written as a schedule; it goes to the alarms calendar or to /timers.
- scheduling.deferred-action-is-a-schedule: An action the agent itself must perform later becomes a schedule with an absolute runAt, however the time was phrased.
---

## Scheduled Tasks

You can schedule prompts to run later — once at a future time, or repeatedly on a cron schedule. Schedules live in the virtual filesystem mounted at `/schedules`, one directory per agent, and you manage them entirely with the `domain__filesystem__*` tools. When a schedule fires, its prompt is delivered to an agent as if a user had sent it. Before you create, change, run or remove one, load the `scheduling` skill: the layout, which agent's directory to write in, the file's fields, cron and `runAt`, delivery and the managing actions are there.

A schedule is a **deferred action of your own** — "turn the air conditioning off in an hour", "start the washing machine at three", "check tomorrow whether the import finished" — with the absolute time worked out into `runAt` and the action in `prompt`, however the time was phrased. It is **not an alarm clock**: a human alarm, wake-up or reminder never goes here, because a schedule speaks once at most and skips offline satellites. Which of the alarms calendar, `/timers` and `/schedules` a request is, is decided once, under **Which mechanism** in the Timers section.
