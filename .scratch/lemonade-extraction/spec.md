# Lemonade extraction: a turn's memory extraction follows its Lemonade model

Status: ready-for-agent
Date: 2026-09-02
Glossary: `CONTEXT.md` → Models (patchable model, Lemonade chat host, Lemonade model)
Supersedes: `.scratch/lemonade-chat-host/spec.md` user story 33 and its "memory extraction" out-of-scope line

## Problem Statement

A person picks a Lemonade model for a turn so that the turn stays on their own box and off the
paid provider. The turn does, but the memory extraction that follows it does not: it always goes
to the configured OpenRouter extraction model, so a conversation the person deliberately kept
local still sends its content to a hosted provider, and pays for it, on every turn.

## Solution

When a turn's patch names a Lemonade model, the memory extraction for that turn is sent to the
same model on the Lemonade chat host, with the extraction schema enforced by the box. Turns
without a Lemonade patch keep the configured extraction model exactly as today. A Lemonade
extraction waits a short fixed grace before it is sent, so the person's own answer reaches the
box's single slot first, and other extractions are not held behind it. If the box cannot answer
after the worker's retries, the batch falls back to the configured extraction model, and the
switch is logged and counted. Dreaming, recall, subagents and every other channel are untouched.

## User Stories

1. As a WebChat user, I want the memory extraction for a turn I sent to a Lemonade model to run on that same model, so that a conversation I kept local does not leak to a hosted provider.
2. As a WebChat user, I want a turn without a Lemonade model to be extracted by the configured model as before, so that switching back to OpenRouter changes nothing about memory.
3. As a WebChat user, I want the extraction to follow the model I picked for each turn, not a sticky choice, so that the behaviour matches what the gear menu says for that message.
4. As a WebChat user, I want my answer to reach the box before the extraction does, so that picking a local model does not make every reply wait for a memory pass.
5. As a WebChat user, I want a Lemonade extraction to still remember what I said if the box goes away between my turn and the extraction, so that a flaky box costs me a local extraction, not a memory.
6. As a WebChat user, I want a Lemonade model that vanished from the box by extraction time to be replaced by the configured model, so that an extraction never waits on a model that is not there.
7. As a WebChat user, I want the extraction on the box to produce the same kind of memories as the hosted extractor, so that what is remembered does not depend on where it was extracted.
8. As a WebChat user, I want a long turn with large tool results to still be extracted on the box, so that an oversize window is cut rather than failed.
9. As a WebChat user, I want the Lemonade model to think during extraction the way it does by default, so that memory quality is the model's own and not throttled.
10. As an operator, I want the grace period in the agent's settings, so that I can shorten it on a fast box or lengthen it for long tool-heavy turns without touching code.
11. As an operator, I want the grace to ship with a sensible default, so that a deployment that never looks at it behaves well.
12. As an operator, I want a Lemonade extraction's token usage charted under the Lemonade model's name at zero cost, so that the token and cost charts stay truthful and never mix local and hosted models.
13. As an operator, I want a warning in the agent's log and an error metric when an extraction falls back from the box to the configured model, so that a box that keeps failing extractions is diagnosable.
14. As an operator, I want a fallback to be visible as a fallback and not as a silent success, so that I know a local extraction did not happen.
15. As an operator, I want the box's retries and the fallback to reuse the worker's existing retry count, so that there is one knob for how hard extraction tries.
16. As an operator, I want a deployment without a Lemonade chat host to behave exactly as today, so that the feature cannot affect a stack that never configured a box.
17. As an operator, I want dreaming to keep its configured OpenRouter model, so that the nightly sweep over every user never lands on somebody's box.
18. As an operator, I want an extraction that waits out its grace not to hold up extractions for other turns and users, so that one local conversation cannot slow memory for everyone.
19. As a developer, I want the extraction to route by the same Lemonade id convention a turn uses, so that routing, metrics and whitelisting agree by one rule.
20. As a developer, I want extraction to reuse the routing chat client and the Lemonade client a turn uses, so that the box's wire quirks are handled in one place.
21. As a developer, I want the box to enforce the extraction schema, so that the parser is not left guessing at the shape a local model felt like producing.
22. As a developer, I want the schema rewrite pinned by a test, so that a change to the Lemonade client cannot silently stop enforcing it.
23. As a developer, I want the OpenRouter extraction path byte-for-byte unchanged, so that the hosted extractor keeps working exactly as before.
24. As a developer, I want the extraction request to carry the model the person picked, so that the worker does not have to re-read the thread to find out.
25. As a developer, I want the grace tested against a controllable clock, so that the test does not sleep.
26. As a developer, I want Telegram, voice and the other channels untouched, so that a channel with no per-turn model override sees no change.
27. As a developer, I want subagents untouched, so that a subagent spawned during a Lemonade turn keeps its configured model.
28. As a developer, I want the extractor no longer named after a provider, so that the name does not lie once it serves two hosts.

## Implementation Decisions

### What "selected" means

- The trigger is the turn's config patch. The recall hook, which already runs after the patch is stamped on the user message and already builds the extraction request, copies the patch's model onto the request. A patch naming an OpenRouter model, or no patch, leaves the request's model empty and the extraction goes to the configured extraction model as today.
- Nothing is sticky server-side. Every turn's extraction decides on its own patch, matching the turn's own resolution.
- Only WebChat sends a patch, so every other channel is unchanged by construction.

### The extraction request and the extractor

- The extraction request gains one optional field: the model the person picked, in the namespaced Lemonade form the rest of the system uses. It is set only when the patch names a Lemonade model.
- The extractor's single call gains the model as an optional argument. When given, the extractor sets it as the model on the chat options it sends; when absent it sends no model and the chat client's own default applies. Everything else about the extractor's prompt, schema and parsing is unchanged.
- The extractor is renamed to stop naming a provider; the consolidator keeps its name because dreaming stays on OpenRouter.

### Routing

- The memory module builds the extractor's chat client as the same routing client a turn uses: the existing OpenRouter client for the configured extraction model, and, when a Lemonade chat host is configured, a Lemonade client for that host. The router picks by the model on the options, exactly as a turn does. With no host configured the extractor is built exactly as today.
- The Lemonade client for extraction is built with the same "smaller of the two windows" rule a turn uses, taking the extraction pipeline's window and the model's discovered window. That rule moves out of the agent factory into a shared place so both callers read one function.
- No reasoning effort is sent for an extraction on the box; the model thinks as it does by default.

### Schema enforcement on the box

- The Lemonade client's wire rewrite, which already rewrites the model id and drops replayed reasoning items, also moves a JSON-schema response format from the Responses wire's `text.format` field into a chat-completions-shaped `response_format` field on the same body. The box ignores the former and enforces the latter, verified on the real box streamed and non-streamed, with thinking on and off. Any other `text.format` shape is left alone.
- This applies to every Lemonade request, not only extraction: a turn never carries a response format today, so a turn is unaffected.
- The pass-through is undocumented on the box's side. The rewrite is pinned by a unit test; whether the box keeps honouring it can only be checked live, and the spec notes it as a known risk.

### The grace

- A request naming a Lemonade model is not sent until a fixed grace has passed since it was enqueued. The grace is one integer setting, in seconds, under the memory extraction section of the agent's settings, defaulting to 30. It is a generic tunable and lives in the agent's appsettings alone.
- The worker measures the grace with the host's time provider, so a test can drive it with the repository's armed clock.
- A delayed request waits off the worker's main loop: other requests keep flowing, and two extractions for the same user may then run concurrently, which the store's novelty check already tolerates. A request with no Lemonade model is processed immediately, as today.
- A Lemonade model that the model source no longer lists when the grace expires is treated as absent: the request is extracted with the configured model, with a debug log line.

### Failure and fallback

- The worker's existing retries run against the box first. If every attempt ends in the named Lemonade host error, the worker extracts the same window once more with no model, which is the configured extraction model, and proceeds as today.
- The fallback is marked: one warning log line naming the box's address and the model, and one error metric under the memory service, published before the fallback attempt. The extraction event that follows is the fallback's, so a fallback never reads as a silent success.
- Only the named host error triggers the fallback. A parse failure, a store failure or any other exception keeps its existing path.
- The fallback attempt itself gets no further retries beyond what the existing retry loop gives an OpenRouter extraction.

### Metrics

- A Lemonade extraction's token usage and truncation events carry the namespaced Lemonade id and zero cost, which the reused Lemonade client already does for a turn. The extraction event itself is unchanged.

### Configuration

- One new setting, the grace in seconds, in the agent's appsettings under the memory extraction section. No compose change, no `.env` entry, no shared policy file.

### Documentation

- The glossary's Lemonade chat host entry has already been amended: recall talks to the in-stack Lemonade, and extraction's fallback is the one exception to "nothing answers in its place".
- The earlier Lemonade chat host spec's story 33 and its "memory extraction" out-of-scope line are superseded by this spec, and a note saying so is appended to that spec's comments.

## Testing Decisions

A good test drives a seam from outside and asserts what a caller, an operator or the box would observe: which model an extractor was asked for, what bytes reached a stubbed host, what was published, what a request in the queue carried. No test reads private fields, sleeps, or asserts on which inner client was chosen except through the URL the request went to. Four existing seams, no new ones:

1. **Extraction worker.** Driven through the worker's process method and its queue with a mocked extractor, exactly as the existing worker tests do. A request naming a Lemonade model is extracted with that model. When every attempt throws the named host error, the extractor is asked once more with no model, a warning is logged and an error metric is published before the fallback, and the extraction event counts the fallback's candidates. Any other exception does not fall back. A Lemonade request enqueued at time zero is not extracted before the grace, is extracted once the armed clock passes it, and a plain request enqueued behind it is extracted straight away. A Lemonade model absent from the model source at send time is extracted with no model. Prior art: the existing worker tests, the recording metrics publisher, the capturing logger, and the armed clock.
2. **Recall hook.** The hook enqueues a request that carries the user message's patch model when it names a Lemonade model, and no model when the patch names an OpenRouter model or is absent. Prior art: the existing hook tests that already inspect the enqueued request's anchor and fallback content.
3. **Lemonade wire.** Through the routing client over the capturing Lemonade stream handler: a request whose chat options carry a JSON-schema response format reaches the host with a `response_format` node holding the same name, schema and strictness, and with no `text.format` node; a request with no response format reaches the host as today; an OpenRouter request with the same response format reaches OpenRouter byte-for-byte as the bare client sends it. A Lemonade extraction whose window exceeds the discovered model window is truncated to it, stamped under the namespaced id. Prior art: the routing client tests, the Lemonade turn fit tests and the captured-stream handler.
4. **Composition.** The memory module resolves an extractor when a Lemonade chat host is configured and when it is not, and the extractor built with no host sends to OpenRouter exactly as before. The agent settings test pins the grace default at 30 seconds. Prior art: the existing memory module tests and the agent settings tests.

No live-box test and no Playwright E2E: the E2E stack has no Lemonade chat host, and the schema pass-through can only be confirmed by hand against a real box.

## Out of Scope

- Dreaming on a Lemonade model. Naming one as the dreaming model stays a configuration error.
- A Lemonade model as an agent's default, for subagents, or as a deployment-wide extraction model.
- Recall and embedding, which speak to the in-stack Lemonade and are unaffected.
- Any channel other than WebChat.
- Enqueueing the extraction after the answer is persisted instead of after a grace; noted as the follow-up if the grace proves wrong.
- Sending a reasoning effort, or following the turn's effort, for extraction on the box.
- Falling back for anything other than the named host error.
- A chat-completions client for the box.
- Changing the extraction prompt, schema, parser or window size.
- A Playwright E2E with a stub Lemonade host.

## Further Notes

- Extraction runs concurrently with the turn, not after it: the recall hook enqueues before the agent sees the message, and the window is prior history plus the raw current user text. The grace exists because of this, and because the box has one slot and queues rather than refuses a second request (verified: two concurrent requests both succeeded, the second after waiting).
- The conversation's prompt cache on the box survives an interleaved unrelated request (verified: 7020 of 7024 tokens still cached after one), so an extraction on the same box does not cost the next turn its prefix.
- Facts from the box that decided the wire (Lemonade 11.8.0, llama-server, one slot): `text.format` json_schema on the Responses wire is ignored in every run; `response_format` json_schema on the Responses body is enforced streamed and non-streamed, thinking on or off; thinking is switched off by `reasoning_effort: "none"`, and a `/no_think` marker in the prompt does nothing.
- One thinking-on structured sample returned a valid but empty candidate list. Nothing follows from one sample; extraction quality on the box is worth watching once it runs for real.
- The fallback breaks the turn rule's symmetry on purpose: a turn that asked for the box fails when the box is away, an extraction that asked for it is answered by the configured model instead. The person chose local to keep the turn local; losing the memory as well was judged the worse outcome.
