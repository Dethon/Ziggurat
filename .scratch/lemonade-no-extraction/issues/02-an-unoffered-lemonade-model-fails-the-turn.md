# 02 — A Lemonade model the host does not offer fails the turn

**What to build:** A person who asks for a Lemonade model that the Lemonade chat host does not
offer is told their box could not serve the turn. Today the turn is silently answered by the
agent's own hosted model instead, and the only record is a log line nobody reads — so the person
asked for their own machine and got the cloud without knowing.

This matters most during an outage rather than for stale picks: the offered list is recomputed
from live discovery, and discovery fails closed, so any outage of the box empties it and *every*
Lemonade model becomes unoffered. Those turns now fail. That is the accepted trade — never routing
a local turn to a hosted provider unannounced — and it is the thing most likely to be reported as
a regression by someone who has not read ADR 0042.

Raise the existing Lemonade chat host failure rather than inventing a type: its construction
already defeats the WebChat transient-error filter, so the person actually sees it.

**The scoping guard is load-bearing.** Model-override resolution shares its rejection path with
reasoning-effort resolution. Only a rejected Lemonade model may throw. A rejected hosted model id
and an unrecognised reasoning effort must keep today's warn-and-continue — widening this would
turn every client with a drifted model list into failed turns.

Follow Red-Green-Refactor. The host-routing chat client's unit tests already drive a real agent
through a streaming run with a message patched to a Lemonade model against stubbed hosts,
asserting on the bytes each host received; an unoffered model is a variation on that setup.

**Blocked by:** None — can start immediately. Independent of 01: this touches the agent's model
resolution, not the memory subsystem, and does not read 01's gate.

**Status:** done

- [x] A turn patched to a Lemonade model the agent does not offer raises the Lemonade chat host
      failure.
- [x] On that failure the hosted stub captured nothing — the turn is not answered in its place.
- [x] The failure reaches the person rather than being swallowed as a transient error.
- [x] A hosted model id the agent does not offer still falls back quietly to the agent's own
      model, exactly as today.
- [x] An unrecognised reasoning effort still falls back quietly to the agent's own effort, exactly
      as today.
- [x] A Lemonade model the agent *does* offer still reaches the host; the existing tests covering
      this stay green.
- [x] A deployment with no Lemonade chat host configured is unaffected.
