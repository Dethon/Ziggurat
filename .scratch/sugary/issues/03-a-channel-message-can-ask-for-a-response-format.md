# 03 — A channel message can ask for a response format

**What to build:** Ziggurat core. A channel message may carry a response format: a schema name and a strict JSON schema. It travels beside the agent config patch, applies to that turn's final answer only and changes nothing about the agent. The agent puts it on the turn's chat options and the chat client sends it on the Responses wire, with provider routing asking for providers that honour the parameter. A turn with a format may still call tools first. See the spec's "Response format per message".

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] A message carrying a response format produces a request whose final answer is constrained to that schema; a message without one produces a request with none.
- [x] The format does not carry over to the next turn of the same conversation.
- [x] Provider routing on such a turn requires providers that support the parameter.
- [x] Tests at the chat client's wire, beside the hydration-depth and config-patch tests.
- [x] An ADR records the per-message response format on channel messages.

## Comments

**Built.** `ResponseFormat` (`Domain/DTOs/Channel/ResponseFormat.cs`: `Name`, `Schema` as a
`JsonElement`) is a new optional property on `ChannelMessageNotification` and `ChannelMessage`.
`McpChannelConnection` copies it across, `ConversationGroup` stamps it on the turn's user message
beside the config patch (`ChatMessage.SetResponseFormat` / `GetResponseFormat`), and `McpAgent`
reads it off the turn's own user message, puts `ChatResponseFormat.ForJsonSchema(schema, name)` on
that turn's `ChatOptions` and marks it strict. `OpenRouterChatClient` sends the schema verbatim with
`strict: true` in `text.format`, and `OpenRouterHttpHelpers` adds `provider.require_parameters: true`
to any request carrying a strict `json_schema` format, beside the configured routing. ADR:
`docs/adr/0044-a-channel-message-may-ask-for-its-answer-in-a-schema.md`; `.claude/rules/channels.md`
and `openrouter-routing.md` each got a paragraph.

**Tests.** Wire tests in
`Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientResponseFormatTests.cs` (a real
`McpAgent` over a real `OpenRouterChatClient`, bodies read off the HTTP handler): format sent strict
and verbatim; none without one; no new top-level field; `require_parameters` beside nabu's shipped
pin and absent without a format; the next turn on the same conversation, whose history still holds
the formatted message, asks for none; a formatted turn calls a tool and both requests carry the
format. Plus the helper matrix in `OpenRouterHttpHelpersTests`, a protocol round trip, the connection
mapping, the monitor stamp, the extension read-back, and a Lemonade case in
`HostRoutingChatClientTests`.

**Decisions.**
- *The schema is sent as written.* MEAI's strict-mode transform moved `minimum`/`maximum` into the
  description. `OpenRouterChatClient` pre-sets the text format through `RawRepresentationFactory`
  instead (the adapter only fills a format nobody set), so ticket 04's schema must already satisfy
  strict mode: every property in `required`, `additionalProperties: false` on every object. The
  standalone Sugary schemas already do.
- *`require_parameters` is keyed on a strict schema, not any schema.* Memory extraction and
  consolidation send non-strict schemas through `OpenRouterChatClient` and read leniently; their
  routing is unchanged.
- *The local box never sees `provider`.* `LemonadeChatClient` strips it; the format itself still goes.
- *The format stays on the persisted user message* (like the config patch) as the record of what the
  turn asked for; the agent reads only the turn's own messages, so it never carries over.
- *Workers don't inherit it* — `SubAgentRunTool` was not touched.
- Not done: the schema is not counted in `MessageTruncator`'s fixed overhead (noted in the ADR).

**How a channel server sets the format (for ticket 04).** Set `ResponseFormat` on the
`ChannelMessageNotification` it emits through its `ChannelNotificationEmitter`, on every message
whose reply it will parse — it is honoured for that message only:

```csharp
new ChannelMessageNotification
{
    ConversationId = conversationId,
    Sender = "app",
    Content = text,
    AgentId = "sugary",
    ConfigPatch = isLabel ? new AgentConfigPatch { Model = labelModel } : null,
    ResponseFormat = new ResponseFormat
    {
        Name = "meal_estimate",                       // or "nutrition_label"
        Schema = JsonSerializer.SerializeToElement(schemaNode)
    },
    Attachments = attachments
}
```

On the wire (camelCase, `ChannelProtocol.SerializerOptions`) that is
`"responseFormat": {"name": "meal_estimate", "schema": { ... }}`. The reply comes back as text
chunks like any other; the channel parses the concatenated text against its schema.
