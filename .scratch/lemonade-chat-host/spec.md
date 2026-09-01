# Lemonade chat host: Lemonade models in the model selector

Status: done
Date: 2026-09-01
Glossary: `CONTEXT.md` → Models (patchable model, Lemonade chat host, Lemonade model)

## Problem Statement

A person using WebChat can switch one turn to a different model with the gear menu, but every
model on offer is a hosted OpenRouter model. They now have a machine on the local network running
Lemonade with chat models loaded on it, and they want to pick one of those the same way they pick
any other model, with nothing to configure beyond the machine's address. That machine is not the
Lemonade the deployment already runs for voice, dictation and memory: it is somebody's own box,
outside the compose stack, and it may be off.

## Solution

The agent host learns the Lemonade chat host's address from configuration. It asks the host which
chat models it has and offers them as patchable models to every agent that offers any, each one
marked with a lemon in the selector. Picking one sends that turn to the Lemonade chat host instead
of OpenRouter, with the agent's tools, history and reasoning effort intact. When the host is not
there, a turn that asked for it fails with a clear error and nothing answers in its place; when it
cannot be asked for its models, the lemon entries are simply absent. With no address configured
the feature does not exist.

## User Stories

1. As a WebChat user, I want the Lemonade chat host's models to appear in the model dropdown of the gear menu, so that I can pick a local model for a turn the same way I pick any other.
2. As a WebChat user, I want a lemon icon before each Lemonade model's name in the dropdown, so that I can tell at a glance which models run on my own box.
3. As a WebChat user, I want the lemon to also precede the model name in the gear button's summary when a Lemonade override is active, so that I know the current turn will go local without opening the menu.
4. As a WebChat user, I want the Lemonade models to appear for every agent that offers patchable models, so that I do not have to remember which agent lets me go local.
5. As a WebChat user, I want a readable display name for a Lemonade model, so that a long quantized filename does not fill the menu.
6. As a WebChat user, I want two Lemonade models that would trim to the same name shown by their full ids, so that I never pick the wrong quantization.
7. As a WebChat user, I want a Lemonade turn to keep the agent's tools, so that the local model can act, not just talk.
8. As a WebChat user, I want the reasoning effort I picked to reach the Lemonade model, so that a local reasoning model thinks as hard as I asked, and "none" turns thinking off.
9. As a WebChat user, I want to attach images to a Lemonade turn only when that model can see them, so that the composer refuses what the model would drop.
10. As a WebChat user, I want a long conversation to still work on a Lemonade model, so that switching to a local model does not fail just because the history outgrew its window.
11. As a WebChat user, I want a clear error in the chat and a toast when the Lemonade chat host is unreachable, so that I know why the turn produced nothing and can switch back.
12. As a WebChat user, I want the failed turn not to be answered by OpenRouter instead, so that a turn I sent to my own box never silently goes to a paid provider.
13. As a WebChat user, I want the Lemonade models to appear within about a minute of loading a new model on the box, so that I do not have to restart anything.
14. As a WebChat user, I want the lemon entries to vanish quietly when the box is off, so that the menu is not cluttered with models that cannot answer.
15. As a WebChat user, I want the default model of my agent untouched, so that only the turns where I picked a Lemonade model go local.
16. As an operator, I want to set the Lemonade chat host's address in one place, so that a deployment points at the box without touching code.
17. As an operator, I want an empty address to mean the feature is off, so that a deployment without a local box sees no lemon entries, no probes and no warnings.
18. As an operator, I want no compose service for the Lemonade chat host, so that the stack does not try to run something that lives on another machine.
19. As an operator, I want the host's health on the observability dashboard, so that a red row tells me the box is down before a user does.
20. As an operator, I want a warning in the agent's log when discovery fails, so that an unreachable box is diagnosable.
21. As an operator, I want a Lemonade model's usage in the token, latency and truncation metrics under a name that says it is Lemonade's, so that charts never mix local and hosted models.
22. As an operator, I want a Lemonade turn to report zero cost, so that the cost chart stays truthful.
23. As an operator, I want an optional API key for the Lemonade chat host, so that a box that requires a bearer token can still be used, and one that does not needs no key.
24. As an operator, I want only chat models that advertise tool calling and are downloaded to be offered, so that a user cannot pick an embedding model or one the box would first have to download.
25. As an operator, I want the models the box proxies from cloud providers excluded, so that "Lemonade" always means the local box.
26. As a developer, I want the Lemonade model id to carry its origin everywhere inside the system, so that routing, whitelisting and metrics agree by one convention.
27. As a developer, I want the OpenRouter turn path unchanged, so that provider routing, sticky sessions and cost accounting keep working exactly as before.
28. As a developer, I want a second Lemonade turn while the box is busy to fail like any other host failure, so that nothing in-process serializes or queues local turns.
29. As a developer, I want the whitelist of patchable model ids to accept discovered Lemonade ids, so that a model that appeared after the agent was built is still honoured.
30. As a developer, I want the model dropdown to be a custom listbox like the agent selector, so that an icon can sit beside each entry.
31. As a developer, I want the lemon drawn as an inline SVG glyph, so that the drawn-icon conformance test keeps passing.
32. As a developer, I want Telegram and the other channels untouched, so that a channel with no per-turn model override sees no change.
33. As a developer, I want memory extraction, dreaming and subagents untouched, so that they keep their configured OpenRouter models.

## Implementation Decisions

### Configuration

- A new top-level settings section, `lemonadeChat`, in the agent host's appsettings, holding `apiUrl` and an optional `apiKey`. `apiUrl` defaults to empty, and empty means the feature is off: no discovery, no whitelist entries, no probe, no warning.
- The address is a per-deployment value, so it gets a placeholder `environment` entry in compose for the agent container and, for the health probe, the observability container. It gets no compose service, no `.env` line unless an API key is set, and no shared policy file. The key is a secret and, when used, follows the secrets rule.
- The observability dashboard's HTTP probe list gains one entry for the host's health endpoint, read from the same address. Absent address, absent probe.
- The optional API key is sent as a bearer token when set and the header is omitted when blank, following the precedent of the memory embedding client.

### Discovery

- A Lemonade model source asks the host's models endpoint and keeps the last answer. It refreshes on its own timer, about a minute, via a background service modelled on the existing model capability refresher, and it also refreshes once at startup.
- A model is offered when its labels include both `chat` and `tool-calling`, it is downloaded, and it names no cloud provider.
- From each kept model the source takes: the id; the accepted attachment kinds (an image kind when labelled `vision`, nothing otherwise); and the context window, `context_length` when present else `max_context_window`, else unknown.
- Display name: the id trimmed at the first `-GGUF`. If two kept ids trim to the same name, both are shown by their full id. Trimming is display only; the id is never altered.
- When the host cannot be asked (unreachable, non-2xx, unparsable), the source offers nothing, logs one warning, and keeps offering nothing until an answer arrives. It does not keep a stale list from a previous answer: an absent host offers no models.
- Discovery is a real network operation done only by the source's refresh; the catalog build reads the source's current answer and never blocks on the network.

### Catalog

- The agent catalog builder takes the Lemonade model source in addition to the configured patchable models and the capability catalog. Lemonade models are appended, after the configured ones, to the patchable list of every agent, as patchable models carrying their namespaced id, display name and accepted attachment kinds. Nothing is added when the source offers nothing.
- The channel connection already re-reads the catalog on every health tick and re-registers when the fingerprint changes; that is how a change in the Lemonade list reaches WebChat. No new hub call.
- A Lemonade model is never an agent's default model. The agent definition and spec projection are not taught to accept one; a definition naming one is a configuration error and is not a target of this change.

### Model identity

- Inside the system a Lemonade model is `lemonade/<id>`: in the catalog, in the config patch, in the resolved turn override, in chat options and in every metric event. The prefix is stripped only when the request body is written, so the box receives the id exactly as it advertised it. The same convention as the existing `~` alias prefix: one string, recognised by shape.
- The patchable model whitelist an agent checks a patch against is no longer fixed at agent construction. It resolves per turn against the configured ids plus the source's current Lemonade ids, so a model discovered after the agent was built is honoured and a model that disappeared is refused with the existing warning-and-fallback.
- The patchable model DTO gains no provider field. The client and the metrics derive "is Lemonade" from the prefix.

### Turn routing

- The chat client an agent is built with becomes a routing client that owns two inner clients: the existing OpenRouter client, unchanged, and a Lemonade client for the configured host. Per call it reads the effective model id from chat options and routes by prefix. An OpenRouter turn goes through exactly the path it goes through today.
- The Lemonade client speaks the OpenAI Responses wire, like the OpenRouter client, with the same reasoning options. This was verified on the real box, which is why the docs listing no tools for that endpoint did not decide it: tools, streamed function-call events, the function-call-output round trip, usage, and nested `reasoning.effort` (`none` disabling thinking) all work.
- The OpenRouter-only body fields `session_id` and `usage.include` are left in the request unless the box rejects them; the box tolerated them. No `provider` node is sent, because the Lemonade client is built with no provider routing, and the advisories do not run for Lemonade turns.
- Truncation becomes per turn. A Lemonade turn truncates to the smaller of the agent's context window and the model's discovered window; an unknown model window falls back to the agent's. An OpenRouter turn keeps the agent's window as today.
- Metrics events from a Lemonade turn stamp the namespaced id as the model, cost zero, and the model window used for truncation. The box reports its served model as a file path in the completion; that value is never used.
- No fallback and no pre-flight: a request that fails to connect, times out or gets a non-2xx from the host fails the turn. No in-process serialization: the box has one LLM slot and closes the connection on a request that arrives while it is busy, and that second turn simply fails.
- Connection failures and non-2xx responses from the host become one named error whose text names the Lemonade chat host and its address, and whose wording the WebChat transient-error filter does not match, so it reaches the toast and the red bubble. The existing turn error path (error content on the stream, no persistence of the failed turn) is reused.
- Images inside tool results ride the Responses wire as they do today; whether a local model reads them is the model's business.

### WebChat client

- The model dropdown in the gear menu changes from a native select to a custom listbox with the same pattern the agent selector beside it uses (roles, keyboard, selection). Each entry shows the display name; entries whose id carries the Lemonade prefix show a lemon glyph before the name.
- The gear button's active-override summary shows the same lemon before the model name when the override is a Lemonade model.
- The lemon is a new glyph in the client's icon table and is rendered only through the icon component, never as an emoji or as inline SVG in a component.
- Client-side sanitisation of the patch keeps working by id: a Lemonade id present in the catalog is a valid override, one that vanished is dropped like any other.

### Naming

- "Lemonade" stays the compose service. The new host is the Lemonade chat host everywhere: settings section `lemonadeChat`, log messages, the named error, the probe's name, the glossary.

## Testing Decisions

A good test drives a seam from outside and asserts what a caller or a user would observe: what the catalog contains, what bytes reach a stubbed host, what the client would render for a given state. No test reads private fields, counts internal calls or asserts on which inner client was chosen except through the URL the request went to.

Three seams, highest first:

1. **Catalog.** The agent catalog builder with a stub Lemonade model source: Lemonade entries appended to every agent with namespaced ids, trimmed names, full ids on collision, image kind from the vision label, none when the source offers nothing. The discovery source against a WireMock models endpoint: filter by labels, downloaded and cloud provider; context window precedence; unreachable or malformed answers offering nothing with one warning; empty address never calling out. Prior art: the catalog builder tests and the OpenRouter model capability tests, including their WireMock setup and the capturing logger.
2. **Turn.** The routing chat client over a stubbed transport: a Lemonade override reaches the Lemonade base URL with the bare id in the body and the reasoning effort intact; an OpenRouter override or no override reaches the OpenRouter URL with a body byte-for-byte as today; truncation on a Lemonade turn respects the smaller window; metric events carry the namespaced id and zero cost; a connection failure or non-2xx surfaces as the named error; the agent's whitelist honours a discovered id and refuses a vanished one. Prior art: the OpenRouter chat client tests with their transport handler, the multi-agent factory tests, the recording metrics publisher and the truncation tests.
3. **Client.** Selector and store tests for the lemon marker: the override summary and the entry marking are pure functions of the id, tested like the existing agent settings selectors. The drawn-icon conformance test guards the glyph and the listbox markup unchanged. No new Playwright E2E: the E2E stack has no Lemonade host and stubbing one was judged not worth it.

The agent host's appsettings test pins the section's default (empty address) so the feature ships off.

- The box's `response.completed` carries a response id that the Responses adapter surfaces as a conversation id, and the agent refuses a turn that offers one because the history is its own. The Lemonade client clears it on every update; the id has already become the message id by then. Found against the real box during implementation.
- "Names no cloud provider" is read as any of: a `cloud` label, a `cloud` recipe, or a `provider` field on the model entry, which is how Lemonade's cloud offload marks the models it proxies.
- Zero cost is a property of the wire, not an override: the box reports no `usage.cost`, so the cost tap drains nothing and the event carries zero. A box that ever reported a cost would be charted with it.

## Out of Scope

- A Lemonade model as an agent's default model, for subagents, for memory extraction or for dreaming.
- Any channel other than WebChat; Telegram has no per-turn model override.
- Falling back to OpenRouter, greying out entries, pre-flight probes, or serializing turns to the box.
- Stripping the OpenRouter-only body fields from Lemonade requests, unless the box starts rejecting them.
- Loading, unloading, pinning or configuring models on the box; Ziggurat only asks what is loaded and downloaded.
- Per-model cost or pricing for local models.
- Discovering OpenRouter models; the configured patchable list stays configured.
- A Playwright E2E with a stub Lemonade host.
- Renaming anything about the compose Lemonade service.

## Further Notes

- The decision to use the Responses wire on an endpoint whose documentation lists no tools was settled by probing the real box, not by the docs. If the box is upgraded and tool calling on that endpoint breaks, the documented route is chat completions, and the routing client is the place where a Lemonade inner client speaking a different wire would slot in.
- The box's health endpoint reports one LLM slot; two conversations picking Lemonade at once will see the second fail. That is the accepted behaviour, not a bug to file.
- A cold load of a large model on the box takes minutes; the first Lemonade turn after the box restarts is slow and the request timeout must allow it. Match the existing OpenRouter request timeout unless it proves too short.
- Discovery answers the default models listing, which already excludes undownloaded models; the downloaded flag is still checked so a change in that default does not offer a model the box would first download.
