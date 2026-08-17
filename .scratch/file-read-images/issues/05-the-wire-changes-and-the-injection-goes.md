# 05 — The wire changes and the injection goes

**What was built:** the chat client speaks OpenRouter's Responses API (beta), and an image the
model read now travels inside the tool result that answered the read — the envelope's text
followed by the picture, as `function_call_output` content parts. The injected user message of
issues 03–04 is deleted, and with it the placement rule, the system attribution, the `IsInjected`
flag, and both exclusions it needed. The store, ceiling, aging, placeholders and the forget-once
delete all survive; only the delivery vehicle changed.

Everything material was verified live against OpenRouter before the cutover: image-in-tool-result
on the agents' model, hard 404 (not stripping) for a model without vision — which is why the send
now asks the capability again for the model the turn resolved to — tool-call round trips on both
patchable models, `session_id`/`provider`/`usage` stamping, a 99% prompt-cache hit, and reasoning
deltas arriving as `response.reasoning_text.delta`, which the Responses adapter surfaces as
`TextReasoningContent` natively, retiring the reasoning scrape. Cost and `cached_tokens` are still
tapped off the stream, now from the `response.completed` event. The `OpenAI.Responses` namespace
is `[Experimental("OPENAI001")]` in the SDK; the suppression is explicit and the package pinned.

The evidence, the decision and the rejected injection design are recorded in
`docs/adr/0029-an-image-the-model-reads-arrives-inside-its-tool-result.md`.

**Blocked by:** 04 — An image ages out of view.

**Status:** done
