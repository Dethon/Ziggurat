# 07 — Multi-turn scenarios

Status: ready-for-agent

Every scenario is a single turn against an empty history, so every "needs a conversation"
exemption is structural. Add scripted history to `Scenario` — prior user/assistant exchanges
seeded into the session the way the harness already scripts turns — and write the first
consumer: `subagents.context-bound-work-is-not-delegated`, a turn whose task depends on what an
earlier turn said, asserting the work stayed in place.
