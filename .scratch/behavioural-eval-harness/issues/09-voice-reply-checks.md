# 09 — Voice reply checks

**What to build:** the assertions about what the agent *said*, and the voice family that uses
them.

Deterministic checks: sentence and word limits, and the absence of markdown, emoji, file paths,
entity ids and urls. Plus value retention — the scenario declares the values that must survive
into the reply, accepting any declared spelling, so "eight minutes" may come back as a numeral or
spelled out in the reply language.

The family covers an action confirmed in two words that still repeats the value the user said, a
fact answered as the value alone, a failure stated in one clause with no cause or code, and the
one-word acknowledgement before slow work.

**Blocked by:** 03, 04.

**Status:** ready-for-agent

- [x] A reply over the sentence or word limit fails, quoting the reply.
- [x] Markdown, emoji, a spoken path, an entity id or a url each fail.
- [x] A declared value missing from the reply fails; any declared spelling of it passes.
- [x] The voice family's scenarios pass, each citing its claims. The one-word acknowledgement
      before slow work is exempted rather than covered: it is a separate emission the recording
      does not see, and it waits on the families that make the agent work slowly.
- [x] The checks themselves are proven against fixed reply strings, with no model involved.
- [x] Each scenario was demonstrated red by deleting the prose of the claim it cites. Ten
      demonstrations were run on 2026-08-18 and every one went red — unlike the mechanism family
      of ticket 07, every rule about the shape of a spoken reply is load-bearing.
