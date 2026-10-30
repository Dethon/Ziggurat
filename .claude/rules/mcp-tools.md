---
paths:
  - "McpServer*/McpTools/*.cs"
---

# MCP Tool Rules

MCP tools wrap Domain tools and expose them via Model Context Protocol.

**Filesystem tools are the exception: never write one.** `fs_*` tools and the mount's `filesystem://` resource are derived from the backend by `AddFileSystemTools<TBackend>()` / `AddFileSystemResource<TBackend>()` — the virtual-filesystem rule has the contract.

## Structure

1. Inherit from the corresponding Domain tool; `Name` and `Description` constants come from it
2. `[McpServerToolType]` class attribute, `[McpServerTool]` + `[Description]` method attributes
3. Return `CallToolResult` via `ToolResponse.Create()`

Any file under `McpServer*/McpTools/*.cs` is a working example, `context.TryScope` guard
included.

The scope guard is only needed by tools that cache per-caller state across calls
(`file_search`/`download_file`, the `web_*` browse tools). A stateless tool takes no scope at all.

## Footprints

A model writes several calls in one message, and `ToolApprovalChatClient` hands them all over at
once. Which of them really run at once is decided by what each says it touches — its
`CallFootprint` (`Domain/Tools/CallFootprint.cs`): two calls conflict when what they touch
overlaps and one of them may change it, a conflicting call waits for the earlier-written one, and
everything else runs side by side (`WrittenOrder`). **A tool that declares nothing conflicts with
everything**, so a new tool is correct by default and slow until it speaks.

- A tool that only reads its own server's state says so with the protocol's hint:
  `[McpServerTool(Name = Name, ReadOnly = true)]` (`web_search`).
- A tool that changes something shared declares it in its own `_meta` —
  `[McpMeta(FootprintTemplate.MetaKey, JsonValue = …)]`, the shape `FootprintTemplate` documents:
  `{"reads": […], "changes": […]}`, each a resource path whose `{name}` segments are filled from
  the call's arguments. The browse tools are the example (`BrowserFootprint`): every ref-routed or
  ref-less call claims the whole browse session, a browse claims the one page it opens.
- Domain tools declare theirs where they are built, with `FootprintDeclaration.Declared` — the
  filesystem tools by resolved virtual path (`FileSystemFootprint`), so `fs_*` tools need nothing.
- Never teach the chat client a tool's name. The declaration travels on the function
  (`GetService<FootprintDeclaration>()`), as `ExecReach` does.

## Error Handling

Do NOT add try/catch blocks or `ILogger<T>` for error handling in tool methods — exceptions
propagate to the call-tool filter that `AddToolServer`/`AddChannelServer` installed (the
mcp-hosting rule owns the filter's contract), which logs and returns an error result.

## No MCP Session

`McpServer.SessionId` is always null under the 2026-07-28 protocol, and `ClientInfo.Name` is the
*agent* name, so it collapses every user and conversation into one bucket. Per-caller state is
namespaced by the call's turn, read through `Mcp.Hosting`'s `CallTurn` accessor
(`context.TryScope(out var scope)`, `context.Turn`) — never by parsing the `_meta` yourself. Never fall back when it is absent — return a
`ToolError`, because a shared-bucket fallback leaks state across conversations and a per-request
fallback silently severs multi-call flows.
