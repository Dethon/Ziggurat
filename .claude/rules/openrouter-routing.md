---
paths:
  - "Infrastructure/Agents/**"
  - "Domain/DTOs/ProviderRouting.cs"
  - "Agent/Modules/MemoryModule.cs"
  - "**/appsettings*.json"
---

# OpenRouter Provider Routing

**An agent's `providerRouting` overrides `openRouter.providerRouting` wholesale — never field-by-field**
(`ProviderRoutingResolver.Resolve`, from `AgentSpecProjection`; stamped as `provider` by
`OpenRouterHttpHelpers.PrepareRequestBodyAsync`). Any definition may carry it: a seeded one, a
template's (`provider-routing:`), a person's registered agent.

- **`{}` is not the wholesale opt-out it looks like**: the JSON config provider binds an empty object to
  null, so `declared ?? global` inherits the global. To opt back to balanced routing under a non-empty
  global, use `{"allowFallbacks": true}` (OpenRouter's default anyway; `ProviderRoutingBindingTests`).
- **Balanced routing is the absence of the object**: OpenRouter has no `sort` for it, only "no `sort`
  and no `order`", so the global default ships unset (`AgentAppSettingsTests`). `sort: "price"` is
  deterministically the cheapest provider, not a weighted spread.
- **Two criteria = one `sort` plus a threshold** (`preferredMinThroughput`, `preferredMaxLatency`), which
  deprioritize rather than filter, so they never dead-end a turn; `maxPrice` is the one real ceiling.
  Cutoffs are guarded non-negative and finite at bind time.
- **A subagent names no model and no routing**: `SpawnContext` carries the parent's `Model` and resolved
  `ProviderRouting`, and `AgentSpecProjection.ForSubAgent` reads nothing else; a `subAgents[]` entry keeps
  only its own `reasoningEffort`. On a turn moved to another model, `SubAgentRunTool` writes the turn's
  `ConfigPatchModel` onto the worker's request and the worker resolves it against the parent's whitelist
  (`ForSubAgent`'s `PatchableModels`), so the two cannot disagree.
- **A strict schema requires providers that honour it**: a body with a strict `json_schema` format
  (`docs/adr/0044`) gets `require_parameters: true` in its `provider` node — created if routing is unset,
  still naming no `sort`/`order`. Memory's schemas are not strict and get nothing. `LemonadeChatClient`
  removes `provider` before the body reaches the box.

## Cache and sessions

- **`order` costs the prompt cache**: sticky routing (why every request carries `session_id`) is off when
  `provider.order` is set, so the static prefix is re-sent uncached every turn; `sort` does not. Prefer
  `only` + `sort`. `ProviderRoutingAdvisories` warns for this and for a `:nitro`/`:floor` suffix fighting
  an explicit `sort` — warnings, never throws, because the same path serves runtime-created agents. They
  run per agent construction with no dedupe. `MemoryModule` binds `openRouter.providerRouting` for
  extraction (`PaidChatClients`) and dreaming directly, so those skip the advisories.
- **A session is the conversation's unless the agent shares one**: `session_id` is
  `<agent>:<conversation>` (`AgentSpecProjection`). `sharedRoutingSessionMinutes` (template key
  `shared-routing-session-minutes`; the voice template and the home's Nabu, 30) makes it
  `<agent>:household:<household>:<window>`, so every voice wake shares one warm provider for the window.
  The window is ours because OpenRouter releases a session only after 60 idle minutes or a provider
  error, so a household that kept talking would be held on one provider for good. A conversation keeps
  the id it was built with; a subagent's is fresh per spawn.
- **Anthropic's models are asked to cache; nobody else is**: `PrepareRequestBodyAsync` stamps one
  top-level `cache_control: {"type": "ephemeral"}` only for `anthropic/…` or `~anthropic/…` model ids.
  Not sent to others: it left GLM's and Mistral's second request uncached more often.
  The Responses wire has no per-block directive, so keep it one top-level directive and let the provider
  place the breakpoint (`OpenRouterHttpHelpers.cs:43-47`).

## Retries

**A 429 is waited out, not treated as a blip**: `OpenRouterRetryPolicy` replaces the SDK's
`ClientRetryPolicy` on every `OpenRouterChatClient` pipeline (six retries, `Retry-After` verbatim, else
2 s doubling to 30 s). `maxRetries: 0` disables everything, the 429 included. A mid-stream 429 (SSE error
on a 200) is not retried by anything.

## Lemonade

- **A Lemonade box is its household's.** A household's own box (its `keys` section's `lemonade`) serves
  it alone; the home with none runs on the deployment's `lemonadeChat`, any other household with none on
  none. `ILemonadeHosts` (`HouseholdLemonadeHosts`) reads every household's box from the accounts
  service's `lemonade-boxes` view and discovers each one's models every minute (`LemonadeModelRefresher`).
- `MultiAgentFactory` builds a turn with its household's box alone — whitelist
  (`PatchableModelWhitelist`), host, routing client (`AgentSpec.Lemonade`, a worker carrying its
  parent's) — so a patch naming another box's model is refused, never run on hardware and a key the
  household does not own. The box is part of `AgentAsBuilt.Lemonade`, so a change rebuilds an open
  conversation.
- Pickers learn each household's models from the registration
  (`RegisterAgentsParams.HouseholdLemonadeModels`): `AgentCatalogBuilder.ForHousehold` gives a household
  the configured models plus its own box's, never the registered entries' Lemonade models.
- A non-home box is a public https address (`LemonadeAddresses`, checked on save): the agent dials it
  from inside the deployment, so an address on the deployment's network is refused. A `throughKit` box is
  saved with no address and no key (the kit's `lemonade` forwarder adds its own) and is
  reached at `http://lemonade.<household>.tunnel` over `TunnelRegistry.StreamsTo` onto that household's
  forwarder alone (`LemonadeHost.Transport`, `.claude/rules/tenant-kit.md`, `HouseholdKitLemonadeTests`) —
  discovery and turns alike, so a closed tunnel offers no models and fails a turn as an unreachable box does.
- **A Lemonade turn is not an OpenRouter turn**: a `lemonade/<id>` model (`LemonadeModelId`) is routed by
  `HostRoutingChatClient` to `LemonadeChatClient` with `providerRouting: null`, so no `provider` node and
  no advisories. Every failure is one `LemonadeChatHostException`, thrown from the wire so the retry
  policy never re-sends the turn, and worded so WebChat's transient-error filter cannot swallow it.

## DeepSeek's markup calls

**A reply that is one whole DSML calls block is read back as its calls** (`DsmlToolCalls.RepairAsync`, around every OpenRouter stream): DeepSeek now and then writes a tool call as text in its own markup (`<｜DSML｜ calls>…`) instead of calling — on Baidu, Novita, Wafer and Together alike, so no provider pin avoids it. Words before the marker stream as they come; from the marker the rest is held until the end, then sent as calls (finish `ToolCalls`) or, anything but a whole block, as the text it was. `string="false"` values are JSON. `OpenRouterChatClientDsmlTests`.

## Empty replies

**An empty reply after a tool result is asked for once more** (`OpenRouterChatClient`): no call and no non-blank text (reasoning alone counts as empty), finish `stop` or none, the request ending in a tool result. A provider now and then drops a generated call (Pearl Research on prod: 91 native tokens, 0 passed on, finish `stop`), which ended the turn with no word. Each ask publishes its own `TokenUsageEvent`; `maxRetries: 0` (Lemonade) asks once. `OpenRouterChatClientEmptyReplyTests`.
