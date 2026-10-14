# 04 — A described meal is estimated by the sugary agent

**What to build:** The Sugary server becomes a channel server. From the app, she types a description of a meal and gets an estimate produced by the new `sugary` agent: the channel opens a conversation for the meal, sends a turn asking for the meal estimate's response format, collects the whole reply, parses it against the schema, computes the totals and the range floor (0.6× to 1.6× of the best weight on every item not marked high confidence) and answers the waiting app request. See the spec's "The channel role" and "The agent".

**Blocked by:** 02, 03

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] A `sugary` agent definition with the Sugary server as its only endpoint, memory and subagents off, Gemini 3.8 Flash as its model and Haiku 5.5 among its patchable models.
- [ ] `agentDefaults` routes the Sugary channel's messages to `sugary`.
- [ ] The estimating prompt is a section served by the Sugary server, declared in the prompt manifest with its budget, and carries the never-suggest-doses rule; the staleness and budget tests pass.
- [ ] The server ships a skill explaining her foods and the tables.
- [ ] The channel's delivery policy is `GateOnLive`; its channel receive contract row passes.
- [ ] Totals always equal the sum of the items, and the range floor is applied in code; tests pin both at the server's boundary.
- [ ] A reply that does not match the schema, or a turn that ends with no reply, answers the app with an error and is never shown half-read.
- [ ] The estimate is written in the app's language, English or Spanish.
- [ ] Tests play the agent over MCP against the real server, like the channel receive and send-reply contract tests.
- [ ] The cost of each estimate appears on the dashboard with every other turn.
