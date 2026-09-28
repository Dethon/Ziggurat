# 04 — An unasked exec is screened first

Status: resolved

Spec: `../spec.md` (Decisions: Only exec, Every unasked path, No ambient state, Escalating).
Blocked by: 02, 03.

In `ToolApprovalChatClient.InvokeFunctionAsync`, an exec call that would run unasked (whitelist
or remembered) is screened before it runs:

- `Run` keeps today's notify-and-invoke path.
- `Ask` calls `RequestApprovalAsync` with a `ToolApprovalRequest` carrying `Screen`, then follows
  today's switch.

The reach comes from the session the call runs in, handed over per call. No `AsyncLocal` and no
build-time field, because an agent can hold several sessions.

A path that resolves to no mount, or to a mount with no reach, is not screened. The request text
comes from `context.Messages`, read the way the spec's State says.

`ToolApprovalRequest` gains the optional `Screen` codes. Channels ignore it until ticket 05.

Tests (`ToolApprovalChatClientTests`):

- a flagged `Host` exec is asked with its codes;
- an unflagged exec notifies;
- a remembered exec is screened;
- a non-exec whitelisted tool is never screened;
- a `/ha` exec is never screened;
- concurrent execs in one iteration are screened independently.
