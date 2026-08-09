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

```csharp
[McpServerToolType]
public class McpExampleTool(IDependency dep) : ExampleTool(dep)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        string parameter,
        CancellationToken cancellationToken)
    {
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var scope))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta.",
                retryable: false));
        }

        return ToolResponse.Create(await Run(scope, parameter, cancellationToken));
    }
}
```

The scope guard is only needed by tools that cache per-caller state across calls
(`file_search`/`download_file`, the `web_*` browse tools). A stateless tool takes no scope at all.

## Error Handling

Do NOT add try/catch blocks or `ILogger<T>` for error handling in tool methods — exceptions
propagate to the call-tool filter that `AddToolServer`/`AddChannelServer` installed (the
mcp-hosting rule owns the filter's contract), which logs and returns an error result.

## No MCP Session

`McpServer.SessionId` is always null under the 2026-07-28 protocol, and `ClientInfo.Name` is the
*agent* name, so it collapses every user and conversation into one bucket. Per-caller state is
namespaced with `Domain.Channels.ConversationScope`, which reads the `ConversationContext` the
agent stamps into every `tools/call`'s `_meta`. Never fall back when it is absent — return a
`ToolError`, because a shared-bucket fallback leaks state across conversations and a per-request
fallback silently severs multi-call flows.
