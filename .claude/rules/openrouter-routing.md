---
paths:
  - "Infrastructure/Agents/**"
  - "Domain/DTOs/ProviderRouting.cs"
  - "Agent/Modules/MemoryModule.cs"
  - "**/appsettings*.json"
---

# OpenRouter Provider Routing

Each `agents[]` / `subAgents[]` entry may carry a `providerRouting` object (`sort` ∈
`price`|`throughput`|`latency`, plus `order`, `only`, `ignore`, `allowFallbacks`,
`preferredMinThroughput`, `preferredMaxLatency`, `maxPrice`), overriding
`openRouter.providerRouting` **wholesale** — never field-by-field. It reaches the wire through
the same path as `session_id`: `ProviderRoutingResolver.Resolve` (called from `AgentSpecProjection`,
which puts the resolved value on the agent spec) → `OpenRouterChatClient` →
`WireHandler` → `OpenRouterHttpHelpers.PrepareRequestBodyAsync`, which stamps `provider`.
`{}` is not the wholesale opt-out it looks like: the JSON config provider records an empty
object as a null-valued key, `Get<ProviderRouting>()` returns null for it, and `declared ??
global` inherits the global — opting an agent back to balanced routing under a non-empty global
default needs a value-bearing field that doesn't change routing: `{"allowFallbacks": true}`
binds to a real object that shadows the global wholesale while leaving `sort`/`order` unset,
and `allow_fallbacks: true` is OpenRouter's default anyway (pinned by
`ProviderRoutingBindingTests`).

**Balanced routing is the absence of the object.** OpenRouter has no `sort` value for its
default load balancing (uptime filter, then inverse-square price weighting) — it is only
reachable by sending neither `sort` nor `order`, so the global default ships unset and
`AgentAppSettingsTests` pins it that way. `sort: "price"` is a different thing: deterministically
the cheapest provider, not a weighted spread.

**`order` costs the prompt cache.** Sticky routing — the reason every request carries a
`session_id` — is disabled when `provider.order` is set, so the ~17k-token static prefix is
re-sent uncached every turn. `sort` does *not* disable it. Prefer `only` + `sort` to restrict
the provider set. `ProviderRoutingAdvisories` logs a warning for this and for a `:nitro`/`:floor`
model suffix fighting an explicit `sort`; both are warnings, never throws, because the same path
serves runtime-created agents. The advisories run at agent/subagent construction
(`ProviderRoutingResolver.Resolve`) with no dedupe — agents are constructed per conversation
activation and subagents per spawn, so a tripped advisory repeats for the lifetime of the config,
not once per agent. `MemoryModule` binds `openRouter.providerRouting` for the memory extraction
and dreaming chat clients directly, so those two models skip the advisories entirely.

**Two criteria = one `sort` plus a threshold.** `sort` ranks by exactly one metric; there is no
composite sort. `preferredMinThroughput` (tokens/s) and `preferredMaxLatency` (seconds) are the
second criterion, and they *deprioritize* rather than filter — an endpoint that misses one drops
to the end of the candidate list, so a threshold nobody meets still routes and can never dead-end
a turn. Each takes either a bare number (OpenRouter's shorthand for the p50 cutoff, and what
`ProviderThreshold` emits when only `p50` is set) or `{p50, p75, p90, p99}`. `maxPrice`
(`{prompt, completion}` in $/M tokens, `{request, image}` per unit) is the exception: a real
ceiling that excludes. Cutoffs are guarded non-negative and finite at bind time, because a
negative one is otherwise silent — it deprioritizes nothing and excludes nobody.
