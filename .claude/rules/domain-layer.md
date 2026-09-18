---
paths:
  - "Domain/**/*.cs"
---

# Domain Layer Rules

The innermost layer — pure business logic with no external dependencies.

- NEVER import from `Infrastructure` or `Agent` namespaces, reference framework-specific types (HttpClient, DbContext, etc.), or depend on concrete implementations.
- Only define interfaces for services that Domain needs to consume; single-implementation services used only by Agent layer do not need interfaces here.
- No state management — that's Infrastructure's job. Three carve-outs, each state that both sides of a Domain/Infrastructure seam must share without a parameter to carry it: `Domain/Channels/ChannelInbox.cs` (`Mcp.Hosting` and the channel servers that depend on Domain alone all need it), `Domain/Channels/CallerContext.cs` (the call-tool filter enters the calling turn's `ConversationContext` for the duration of one tools/call, ambient rather than a parameter through every filesystem operation; it holds nothing between calls), and `Domain/Skills/SkillPreloadPending.cs` (a judgment started where the user message is built rides on that message until the skills provider takes it at insertion; the message is persisted so a task cannot be one of its properties, the table is weakly keyed and an entry is taken once, so nothing is held between turns).
