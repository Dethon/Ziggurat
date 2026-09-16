# 0043 — An entity line says what it offers, and a hidden entity is not there

Status: accepted
Date: 2026-09-16

## Context

A voice request, "turn on the TV and put Crunchyroll", took 36 seconds. Every tool call answered
in under 5 ms; the time was ten model round-trips at three to six seconds each, seven of them
spent discovering how the television works. The setup index (`/ha/setup-index.md`) named the
remote and, per class, its actions — but not that `turn_on.sh --activity <app>` is how an app
opens, nor which apps exist. The model read `state.json` three times, ran `--help` five times,
globbed once and made one wrong call before it found `activity_list` on the remote's state. The
index was built on the argument that a class table costs ~350 tokens where a per-entity listing
costs ~4.4k, and a round trip ~1.15 s; the argument still holds, and it also says that the one
fact that saves seven round trips is worth a line.

Two entities made the hunt longer. `button.living_room_tv_wake`, which exists so an automation
can press it when the remote is asked to turn on, was listed under the TV's room; the model
pressed it by hand and then went looking for what else it needed. The TV's Cast endpoint sat
under `unassigned` with a hostname for a name, and two turns went to reading it as a second
device. Home Assistant has a registry flag for the first case — an entity hidden there is off the
home's own dashboards — but the states endpoint the catalog reads serves hidden entities
regardless, so the flag reached nothing here.

## Decision

**A choosing entity's index line carries its choices and the action that takes them.** The
summary appends ` — <service>.sh --<flag>: a, b, c` for the attribute lists that are literally an
action's argument — `activity_list` for `turn_on --activity`, `source_list` for `select_source
--source`, `options` for `select_option --option` — only when the entity admits that action and
the list is non-empty. The header says a segment stops at the dash. The table is deliberately
short: a climate's four mode lists would spend many tokens on a call the model already makes
without help.

**An entity hidden in Home Assistant's registry does not exist to the agent.** The area template
the catalog renders also lists `is_hidden_entity` matches, and the provider drops those states
before the catalog is built. No tree entry, no index line, no path resolves to it. Hiding is done
in Home Assistant, where the person who added the entity decides whether it is for people or for
automations.

The skill body says the rest in two sentences: a line that goes on after the dash is acted on
directly, `turn_on.sh` on a remote or player is how a TV is switched on even when it reads
`unavailable`, and an empty `changed[]` after a command or a launch is a success whose effect
arrives later.

## Consequences

- The ideal path for the request above is two model turns: load the skill and index, then one
  `turn_on.sh --activity Crunchyroll`. On the live house the index grows by one line per entity
  that offers choices — the remote, the players and the vacuum's selects.
- A list that is an argument of some other action goes in the table beside the three, not in
  prose; a list that is not an argument stays out.
- A hidden entity cannot be reached by the agent at all, which is the point: an automation that
  needs it presses it from inside Home Assistant.
- The eval's changed tier selects the home scenarios for the skill edit; the summary change is a
  fixture-level edit it cannot see, so a family run is the check (`docs/adr/0041`).
