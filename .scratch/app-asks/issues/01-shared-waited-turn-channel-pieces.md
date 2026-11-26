# 01: Prefactor: a channel server's waited turns, upload store and follow-ups are shared

Spec: `.scratch/app-asks/spec.md` ("Further Notes"; "Files"; "Conversations").

**What to build:** Sugary's channel already has the pieces a second channel server needs:
- waiting on the reply to a turn it sent, by turn key, with timeout and cancel;
- an upload store, with its sweeper and the `fetch_attachment` tool that serves it;
- binding a conversation it opened to who may continue it, within a follow-up window.

Move these into the shared hosting library so the apps server can use the same code. Sugary behaves
exactly as before.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [x] The shared pieces have no Sugary vocabulary: no meals, estimates or Sugary settings names.
- [x] Sugary uses them; every Sugary channel, estimate, clip and upload test stays green unchanged.
- [x] The follow-up binding is keyed by an opaque owner, so the apps server can key it by app and viewer.
- [x] The upload store keys its references by household, so no other household's reference is ever served.

## Comments

Moved into `Mcp.Hosting` (namespace `Mcp.Hosting`):
- `AgentTurns`, `TurnReply` (from `McpServerSugary/Channel/AgentTurns.cs`). The emit, wait, timeout and
  cancel that `SugaryChannel.AskAsync` did is now `AgentTurns.SendAsync(message, timeout, clock, ct)`,
  which also logs a `Failed`. `PendingTurn` is internal. `SugaryChannel` builds the message and calls it.
- `UploadStore`, `UploadSweeper`, and Sugary's `FetchAttachmentTool` as `UploadStoreFetchTool`, wired by
  `IMcpServerBuilder.AddUploadStore(IUploadStoreSettings, RetentionSettings)`. Sugary's `UploadSettings`
  implements `IUploadStoreSettings` (`StoragePath`), so its keys are unchanged.
- `OpenConversations<TOwner, TCarried>`, the follow-up binding. `OpenEstimates` stays in Sugary as a thin
  adapter keyed by `Account`, carrying the `ReviewCopy`.

Deviation, household keying: the reference id keeps Sugary's `conversation/attachment` shape. The
household is recorded in `meta.json`, and only when one is given (`SaveForHouseholdAsync`). Such a file is
handed only to a read naming that household (`ReadBytesAsync(id, household, ct)`; the fetch tool passes
`call.Household?.Id`). A file saved for none, which is all of Sugary's, is handed to any read, as before,
and its `meta.json` is unchanged byte for byte. Today every `fetch_attachment` call is signed as nobody
(`McpChannelConnection.FetchAttachmentAsync` sends no turn context), so a household's file is refused
until the agent signs the fetch for the turn's household. That is ticket 06's agent side.

Left in Sugary: `SendReplyTool`, which calls `AgentTurns.Receive`, and `RequestApprovalTool`, which
refuses. The apps server needs its own `send_reply` that calls `AgentTurns.Receive`.
