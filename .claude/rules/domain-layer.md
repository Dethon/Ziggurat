---
paths:
  - "Domain/**/*.cs"
---

# Domain Layer Rules

The innermost layer — pure business logic with no external dependencies.

- NEVER import from `Infrastructure` or `Agent` namespaces, reference framework-specific types (HttpClient, DbContext, etc.), or depend on concrete implementations.
- Only define interfaces for services that Domain needs to consume; single-implementation services used only by Agent layer do not need interfaces here.
- No state management — that's Infrastructure's job. Two carve-outs, both transport-protocol state that Domain-only projects must share because they cannot reference Infrastructure: `Domain/Channels/ChannelInbox.cs` (`Mcp.Hosting` and the channel servers that depend on Domain alone all need it) and `Domain/Channels/CallerContext.cs` (the call-tool filter enters the calling turn's `ConversationContext` for the duration of one tools/call, ambient rather than a parameter through every filesystem operation; it holds nothing between calls).
