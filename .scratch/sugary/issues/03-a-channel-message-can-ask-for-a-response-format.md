# 03 — A channel message can ask for a response format

**What to build:** Ziggurat core. A channel message may carry a response format: a schema name and a strict JSON schema. It travels beside the agent config patch, applies to that turn's final answer only and changes nothing about the agent. The agent puts it on the turn's chat options and the chat client sends it on the Responses wire, with provider routing asking for providers that honour the parameter. A turn with a format may still call tools first. See the spec's "Response format per message".

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] A message carrying a response format produces a request whose final answer is constrained to that schema; a message without one produces a request with none.
- [ ] The format does not carry over to the next turn of the same conversation.
- [ ] Provider routing on such a turn requires providers that support the parameter.
- [ ] Tests at the chat client's wire, beside the hydration-depth and config-patch tests.
- [ ] An ADR records the per-message response format on channel messages.
