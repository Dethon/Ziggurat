---
paths:
  - "Domain/**/*.cs"
---

# Domain Layer Rules

The innermost layer — pure business logic with no external dependencies.

- NEVER import from `Infrastructure` or `Agent` namespaces, reference framework-specific types (HttpClient, DbContext, etc.), or depend on concrete implementations.
- Only define interfaces for services that Domain needs to consume; single-implementation services used only by Agent layer do not need interfaces here.
- No registry — a store, table or cache that outlives one operation and is found by key is state management, and that is Infrastructure's job. Two carve-outs, each a table that both sides of a Domain/Infrastructure seam must share without a parameter to carry it: `Domain/Channels/ChannelInbox.cs` (`Mcp.Hosting` and the channel servers that depend on Domain alone all need it) and `Domain/Skills/SkillPreloadPending.cs` (a judgment started where the user message is built rides on that message until the skills provider takes it at insertion; the message is persisted so a task cannot be one of its properties, the table is weakly keyed and an entry is taken once, so nothing is held between turns).
- State scoped to one operation is not a registry: it lives on the object that is the operation, handed to whoever needs it as a value, never looked up from Domain. `LatencyScope` (one measurement) and `VfsCall` (one exec: what the command changed, whether it was revoked) are the pattern. What decides how long such an object is kept, and finds it again by key, is the registry half and stays out — `VfsBridge` in `Infrastructure/Agents` holds the calls by token and owns their expiry, which is why a `VfsCall` does not know when it expires.
