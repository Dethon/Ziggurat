# 0044 — A channel message may ask for its answer in a schema

Status: accepted
Date: 2026-10-14

## Context

Sugary becomes a channel server whose turns are carb estimates (`.scratch/sugary/spec.md`). The
server does not show the agent's reply to anyone: it parses it, computes totals and widened ranges
from it, and hands the result to the app. A reply it cannot parse is an error the person sees, and
a reply it half-parses is a wrong number on a health decision. So the channel has to be able to say,
on the message, what shape the answer must have — and which shape changes per message: a meal
estimate on one turn, a nutrition label on the next, in the same agent.

Structured output already ran through OpenRouter, but only off the agent: memory extraction and
consolidation put `ChatResponseFormat.ForJsonSchema<T>` on their own `ChatOptions`, non-strict, and
read the answer leniently on purpose (`OpenRouterMemoryExtractor` makes every property optional).
No agent turn had ever asked for one, and nothing a channel sends could reach a turn's options
except the per-message config patch (model and reasoning effort).

Two things on the way to the wire would have lost the format quietly. The Microsoft.Extensions.AI
adapter rewrites a strict schema into the subset it assumes strict mode takes, moving `minimum` and
`maximum` into the property's description, where nothing enforces them. And OpenRouter, asked for a
format, may still route to a provider that ignores the parameter and answers in prose — the
standalone Sugary app sends `provider.require_parameters: true` for exactly that reason.

## Decision

**A channel message may carry a response format — a schema name and a JSON schema, always strict —
and it shapes that turn's final answer and nothing else.**

- `ChannelMessageNotification.ResponseFormat` (`Domain/DTOs/Channel/ResponseFormat.cs`: `Name`,
  `Schema` as a `JsonElement`) travels the config patch's path: `McpChannelConnection` copies it onto
  the `ChannelMessage`, `ConversationGroup` stamps it on the turn's user message, and `McpAgent`
  reads it off **the turn's own user message only**, puts it on that turn's `ChatOptions` as a JSON
  schema format, and marks it strict. The agent's definition, model and routing do not change.
- **The persisted message keeps it**, as the record of what that turn asked for, the way it keeps
  the config patch. The next turn reads that history and asks for nothing: the agent never looks
  for a format anywhere but in the messages of the turn being run.
- **The schema goes out as written.** `OpenRouterChatClient` sets the strict text format through
  the adapter's raw options, which the adapter fills only when nobody has, so its strict-mode
  rewrite never runs. A non-strict schema — memory's — keeps the adapter's handling.
- **Only the answer is strict, never the tools.** The agent marks the turn with a key of its own
  rather than the adapter's `strict` key, because that key is one switch for the format and every
  function tool: set, it sent each MCP tool `strict: true` with its parameters rewritten, an
  optional parameter made required. A formatted turn sends its tools byte for byte as any other.
- **A strict schema requires providers that honour it.** `OpenRouterHttpHelpers` reads the body and,
  when it carries a strict `json_schema` format, adds `require_parameters: true` to the `provider`
  node — beside the routing the deployment configured, creating the node only when there was none.
  The shipped pins hold: the requirement narrows the providers the routing already chose among.
- **The format applies to the final answer, not the work before it.** It rides every request of the
  turn's tool loop, and the model may call tools first; only its text answer is constrained.
- **A worker never inherits it.** `SubAgentRunTool` hands a worker the turn's model and nothing else;
  a worker's answer goes back to its parent as a tool result, not to the channel.
- **The local box gets the format and not the routing.** `LemonadeChatClient` removes `provider`
  from what it sends: the box has no providers to choose among.

## Considered options

**A format named in the agent definition.** The `sugary` agent always wants one of two schemas, so
configuration could name them. Rejected: which one is a fact about the message (a photo of a plate
or of a label), the server that parses the answer is the one that owns its schema, and a second
agent per schema would duplicate everything else about it.

**A field of `AgentConfigPatch`.** The patch already travels per message. Rejected because the
patch is a whitelisted override of the agent's own settings — `patchableModels`, the effort list
the WebChat picker offers — and a schema is neither a setting the agent has nor something a
whitelist can bound. It travels beside the patch, not in it.

**Let the adapter's strict transform stand.** It is what `ForJsonSchema` does by default. Rejected:
it turns `minimum: 0` into prose the provider is free to ignore, and the channel's schema is the
contract its parser holds the answer to. The strictness requirements the transform enforces
(`additionalProperties: false`, every property required) are the channel's to meet instead.

**`require_parameters` for every schema.** Simpler to state. Rejected for memory: its readers are
built to tolerate a provider that drops the format, and requiring the parameter there would turn a
tolerated degradation into a failed extraction on whatever the memory model's providers support. A
strict schema is the line, because strict is what a caller that cannot tolerate prose asks for.

**Carry the format on the agent's run options rather than on the message.** Rejected: the message
is the only thing that reaches the agent from a channel, and the config patch already proved the
route — the resolution of both happens in the one place, `ResolveTurnConfig`.

## Consequences

- A channel server sets `ResponseFormat` on the `ChannelMessageNotification` it emits, every time it
  wants a parsed answer. Its schema must already satisfy strict mode, since nothing rewrites it.
- A formatted turn can fail where an unformatted one would have answered: if no provider of the
  resolved model honours the parameter under the configured routing, OpenRouter refuses the request.
  That is the intended trade — no answer, reported as an error, over a reply in the wrong shape.
- What the model sends back is still text. The channel parses it and owns the error when it does
  not match; nothing in the agent validates the reply against the schema.
- The schema rides the persisted user message, so a long conversation of formatted turns stores the
  schema once per turn. It is a few kilobytes at most and never sent again.
- The schema is not counted in the truncation budget's fixed overhead, as tools and instructions
  are. For a schema of this size against today's context windows it is noise; a much larger schema
  would want it counted.
