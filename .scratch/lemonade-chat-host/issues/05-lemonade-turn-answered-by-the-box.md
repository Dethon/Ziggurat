# 05 — A Lemonade turn is answered by the box

**What to build:** A person picks a Lemonade model in the gear menu and sends a message; the reply comes from the Lemonade chat host, with the agent's tools, history and chosen reasoning effort intact. The agent's chat client becomes a routing client that owns the unchanged OpenRouter client and a Lemonade client for the configured host, and picks by the id's prefix per call. The Lemonade client speaks the OpenAI Responses wire (verified on the real box: tools, streamed function-call events, tool-result round trip, usage and nested reasoning effort all work), sends the bare id with the prefix stripped, sends the optional bearer token, and leaves the OpenRouter-only body fields in place unless the box rejects them. Discovered Lemonade ids pass the per-turn whitelist. When the host is unreachable, times out or answers non-2xx, the turn fails with one named error that names the Lemonade chat host and its address and reaches the WebChat toast and red bubble; nothing retries on OpenRouter. A turn on an OpenRouter model is byte-for-byte what it was.

**Blocked by:** 03 — The patchable whitelist resolves per turn from a source; 04 — Lemonade models appear in the selector.

**Status:** ready-for-agent

- [ ] A turn whose override is a Lemonade model reaches the Lemonade chat host's Responses endpoint with the bare id, the agent's tools, and the reasoning options exactly as an OpenRouter turn would carry them
- [ ] A turn with an OpenRouter override, or none, reaches the OpenRouter endpoint with a body identical to today's, and provider routing advisories do not run for Lemonade turns
- [ ] A discovered Lemonade id is honoured by the whitelist; one that has vanished from discovery is refused with the existing warning and fallback
- [ ] A connection failure, timeout or non-2xx from the host surfaces in WebChat as a toast and a red bubble whose text names the Lemonade chat host and its address, and the transient-error filter does not swallow it
- [ ] No fallback and no pre-flight: the failed turn is not re-sent anywhere, and a second Lemonade turn while the box is busy fails the same way
- [ ] Subagents, memory extraction and dreaming still use their configured OpenRouter models
- [ ] Tests: the routing client over a stubbed transport following the OpenRouter chat client tests, and the multi-agent factory tests for the whitelist
