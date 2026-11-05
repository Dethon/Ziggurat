# Removing an agent that still runs Home Assistant watches

Status: needs-triage

Found reading the removal guard in manual test 14 (2026-11-05); not reproduced by hand, since the smoke
stack runs no Home Assistant. Removing an agent is refused while it answers by default, opens a space,
is the last one, answers a household Telegram bot, or runs schedules (adebd3812: "Chef runs 2 schedules,
so it stays"). Watches are not counted: a watch is an automation in the household's own Home Assistant,
reached through its tunnel, and its metadata names the agent that runs it (`HaWatchMetadata.AgentId`).
Removed, the agent's watches stay armed in the home, and every fire is refused as "household has no agent
<id>" (42a7d4077 mints no topic for it), forever and with nobody told — what removing an agent with
schedules did before adebd3812. Removed ids are never reused (c7b642660), so nothing can take them over.

## What is wanted

- The removal asks the household's Home Assistant server how many watches name the agent (a
  `delegations_list`-like count over `/ha/watches`, through the household's tunnel as the key commands
  reach it), and refuses like schedules: "Chef runs 1 watch, so it stays: ask it to delete it first."
- A home that cannot be asked (tunnel down) refuses the removal 503, as an unreadable schedule store does;
  a household with no Home Assistant counts none.

## Open questions

- Or should a watch's fire for a missing agent fall back to the household's default agent, rather than
  refuse? (A schedule's does not.)
