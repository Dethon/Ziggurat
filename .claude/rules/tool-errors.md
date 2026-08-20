---
paths:
  - "Domain/Tools/**"
  - "Domain/DTOs/FileSystem/**"
  - "Infrastructure/Utils/**"
  - "Mcp.Hosting/**"
  - "McpServer*/McpTools/**"
---

# Every tool failure is the same envelope

`{ ok:false, errorCode, message, retryable, hint? }`, from every MCP integration in the repo —
filesystem mounts through `FsResult`/`FsError`, throwing tools through `ToolError.CodeFor` at the
call-tool filter, and channel servers through the same filter's default. A failure answers three
things: **what failed** (code + message), **whether trying again is worth anything** (the code
decides), and **what to do instead** (the hint).

- **`ToolError.Codes` is the whole taxonomy** and `ToolError` is the one table behind it. A code
  nobody declared is not retryable, on the reasoning that a loop on a failure nothing here
  understands is the worse of two guesses. `ToolErrorTaxonomyTests` walks the consts and fails on
  one with no meaning.
- **Retryability belongs to the code, never to the call site.** There is no `retryable` parameter to
  pass: `ToolErrorResult.Retryable` reads `ToolError.IsRetryable(ErrorCode)`, and an envelope read
  back off the wire (`FromEnvelope`) is re-answered by this side's taxonomy rather than trusting the
  flag a foreign server sent. Only `timeout`, `transient_dependency`, `rate_limited` and
  `internal_error` invite a retry. This is what the change fixed: the same code went out retryable
  from one tool and not from another, and an agent learned that a dependency being down was
  sometimes worth waiting for.
- **A failure with a recovery action always names one.** Codes where "no" on its own would send the
  model round the same call in different words carry a default recovery line in the taxonomy;
  `ToolErrorResult.Recovery` prefers the site's own hint and falls back to it, so the wire always
  carries one. `FileSystemBackendBase.Unsupported` builds its hint from the mount's own overrides
  (`FileSystemOperations.SupportedBy`), which is the same reflection the registrar advertises tools
  with — so what a refusal offers cannot drift from what the server offers.
- **One exception mapping, in Domain.** `ToolError.CodeFor(Exception)` is what both boundaries use:
  `ToolResponse.Create(Exception)` for tool servers and `CallToolErrorFilter`'s default for the four
  channel servers, which passed no error result and used to hand back a bare exception message. The
  distinctions are the ones a caller acts on differently — 401 is `authentication`, 403 is
  `permission_denied`, 429 is `rate_limited`, 5xx and a dead socket are `transient_dependency`.
- **"Not available" is four answers, and `CapabilityState` makes the code say which.** `Absent`
  (nothing of that name — `not_found`), `Unavailable` (it exists and is not answering —
  `transient_dependency`, the only retryable one), `Unassigned` (it exists and this caller was not
  given it — `permission_denied`), `Unsupported` (it exists and does not do that —
  `unsupported_operation`). `CapabilityError.For` requires a hint. A machine that registered with
  the hub and did not answer this session is declared on the registry at build time
  (`OutpostEndpoints.DeclareUnreachable` → `IVirtualFileSystemRegistry.DeclareAbsence`), so asking
  for `/laptop` says the machine is not answering rather than that the path does not exist — the
  answer that used to send the model hunting for a spelling mistake in a name it got right.
