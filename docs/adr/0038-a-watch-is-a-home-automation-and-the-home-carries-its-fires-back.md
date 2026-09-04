# 0038 — A watch is a home automation, and the home carries its fires back

Status: proposed
Date: 2026-09-05

## Context

Someone asks the agent, on voice or on Telegram, to "warn me when Laura's sugar passes 180",
"turn the heating down when the window sensor opens", or "tell me when the washing machine
finishes". Nothing in the stack reacts to a home entity changing. The `/ha` mount reads state,
history and statistics and runs the service catalog, but it is a pure tool server whose WebSocket
client opens one connection per command and closes it; `/schedules` is the only thing that puts a
prompt in front of the agent unprompted, and it fires on the clock, not on the home.

A watch — the standing instruction, see `CONTEXT.md` — has to be evaluated somewhere, stored
somewhere, and its consequence has to happen somewhere. The consequence is not only a warning: it
may be a prompt the agent runs, a sentence spoken in a room, or a service the home performs, and
a watch may carry several of them. Two of the three need no agent at all.

## Decision

**A watch is a real Home Assistant automation.** The agent writes it through the automation
config API, Home Assistant persists it, evaluates its triggers and conditions, and runs its
effects. The mount lists the automations that carry the assistant marker at `/ha/watches/<id>/`
as a writable `watch.json` and a read-only `status.json`, and renders the file into the automation
on write. Triggers, conditions and home actions cross as the HA JSON the model wrote; Home
Assistant validates them, so any entity and any trigger the home understands is a watch.

**The home carries a fire back to the stack the way the alarm calendar already does.** A prompt
effect is rendered as a call to a hand-provisioned `rest_command` that posts the rendered prompt
and the firing facts (entity, from, to, when) to a callback endpoint on `mcp-homeassistant`, which
becomes a dual-role server and raises the prompt with the creating agent. An announcement effect
is rendered as the existing `voice_announce` bridge, insistent when asked. A home action is
rendered as itself. The callback emits by broadcast, so an agent that is merely reconnecting still
receives the fire; when nobody has subscribed at all the endpoint answers 503 and the loss is
visible in the automation's trace.

## Considered options

- **The stack watches the home.** `mcp-homeassistant` holds a persistent WebSocket with
  `subscribe_trigger` per watch, stores watches in Redis and raises prompts itself. Everything
  stays in this repository and the eval can drive it end to end, but every watch — including a
  fixed home action and a spoken warning — then depends on the stack being up, the client gains
  reconnection and resubscription logic it has never needed, and the fake socket must learn to
  push frames. Nothing the home already does well is reused.
- **Poll from a schedule.** A cron schedule reads `history.sh` and decides. No new infrastructure,
  but minute-level latency, no crossing semantics (a value that stays above the threshold would
  fire every tick or need our own hysteresis), and a model turn spent on every tick.
- **Fire through an HA event and a persistent socket.** The automation's action fires an HA event
  the stack subscribes to; nothing to provision in the home. Rejected for the same persistent
  socket the first option needs, bought only to avoid one `rest_command` line beside the one the
  alarm bridge already has.

## Consequences

- A watch lives in the home, not in our store. Home Assistant's UI lists it, its traces show each
  fire, and it survives the stack being down — a home action or an announcement fires regardless,
  a prompt effect is lost for that fire and the trace says so. Nothing is retried by us.
- The `/ha` mount gains its first writable subtree. Everything else under it stays read-and-exec.
- Provisioning, not code: the callback `rest_command` joins `voice_announce` in the HA instance's
  configuration, sharing the announce token, and adding it needs a full HA restart there. The
  alarm-bridge document grows a second section; without it a prompt watch is created and never
  reaches the agent, exactly as an alarm without the bridge never rings.
- A one-shot watch turns itself off as its last action and is listed as spent until removed;
  Home Assistant offers no self-delete.
- The eval's fake home must serve the automation config API, and a scenario can assert the
  rendered automation; the callback is an HTTP POST into our own code and is tested as one.
