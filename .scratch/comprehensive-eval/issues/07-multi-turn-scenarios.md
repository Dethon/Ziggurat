# 07 — Multi-turn scenarios

Status: resolved

Every scenario is a single turn against an empty history, so every "needs a conversation"
exemption is structural. Add scripted history to `Scenario` — prior user/assistant exchanges
seeded into the session the way the harness already scripts turns — and write the first
consumer: `subagents.context-bound-work-is-not-delegated`, a turn whose task depends on what an
earlier turn said, asserting the work stayed in place.

## Answer

Done. `Scenario.History` holds scripted `HistoryExchange`s — user text and assistant answer,
verbatim — and `EvalRun.Messages` seeds them into the run as ordinary prior messages: user halves
decorated like the turn's own channel, five minutes apart, oldest first; only the turn carries
the conversation context and the recall block. Pinned in `EvalRunTests` without a model. First
consumer: "work bound to the conversation stays in it" — the doctor's notes live only in the
history, the ceiling is one call, and any delegation is undeclared work — citing
`subagents.context-bound-work-is-not-delegated`.
