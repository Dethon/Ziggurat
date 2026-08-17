# 0029 — An image the model reads arrives as its own message

Status: accepted
Date: 2026-08-17

## Context

The filesystem read tool reads text. A mount can hold a screenshot, a scanned page or a diagram,
and the model has no way to look at any of it — the file is either refused or, worse, decoded as
text. Every other way the agent sees an image already works: a person attaches one and hydration
puts the bytes back on their message.

The obvious fix is for the tool to answer with the image. It cannot. Tool results reach the model
as an OpenAI-style `tool` message, and OpenRouter's schema makes that `content: string` — content
parts are, in its own words, "only for the 'user' role". The bridge we ship agrees: in
`Microsoft.Extensions.AI.OpenAI` 10.8.3, a `ChatRole.Tool` message becomes
`new ToolChatMessage(callId, text)`, where a non-string result is `JsonSerializer.Serialize`d
first, and the content-part list is built only on the `UserChatMessage` branch. Returning a
`DataContent` from the tool therefore sends `{"uri":"data:image/png;base64,..."}` as the tool
message's *text*: no provider decodes pixels from there, and a 500 KB PNG becomes roughly 170k
tokens of gibberish. This is not a limitation to work around later; it is the wire.

So the bytes have to travel on a `user` message. That raises the question the rest of this ADR
answers: where they come from at send time, and who they appear to be from.

Hydration is the machinery for this. It runs on the way to the model, produces a copy thrown away
with the request (ADR-0020), and measures its distance over conversation messages so a
tool-calling loop cannot age an attachment out of its own turn. What it cannot do unchanged is
find the bytes. An attachment's bytes stay with the channel that took the file — `IAttachmentSource`
is fetch-only, keyed by channel and reference — and a read image has no channel. Re-reading the
file from the mount on every send was considered and rejected: the chat client is built per model
from DI while the mount registry is built per session on `ThreadSession`, so the registry would
have to be carried onto every turn's options, and a 15 MB file on an outpost would then be fetched
over MCP from somebody's laptop once per send for as long as it stayed in view.

## Decision

**An image the model reads arrives as its own user message, inserted after the tool result, and
attributed to the system.**

The tool answers a text envelope naming the path, the media type, the size, and whether the image
was shown. On the way to the model, the same pass that hydrates attachments scans each tool-result
message and inserts exactly one user message straight after it, carrying a text label with the
virtual path and then the bytes, once per image read in that batch. Inserting after the *whole*
message rather than between its contents is not a detail: `FunctionInvokingChatClient` builds one
`ChatMessage(ChatRole.Tool, [every result])` per iteration, and some models reject a conversation
where a tool call has not been answered before another message appears.

**Hydration widens rather than splitting.** It is putting bytes back where a reference sits,
whoever put the reference there. One distance rule, one placeholder shape, one pass.

**The bytes rest with the agent.** The tool writes them to a store of the agent's own, keyed by
conversation and tool call, read back by the expansion. The message distance is the real bound —
the key is deleted on the send where the image drops out of view — and a time horizon is the
backstop for a conversation that goes quiet.

**The message is the system's, not a person's.** It carries the system as its sender and is
decorated like any other turn, so the model never reads an image it asked for as something
somebody said to it.

## Consequences

- The tool is renamed `file_read` and its type `VfsFileReadTool`, because a tool that reads text
  and images is not `text_read`. The feature key stays `read` and the wire operation stays
  `fs_read`, so existing config and every outpost binary already copied onto a machine keep
  working; capability strings are derived hub-side from the wire names.
- **No new backend operation.** Images are read through the blob read every disk root already has,
  so `FileSystemOperations.All` is untouched and the tool is offered wherever `fs_read` is. The
  three mounts with no bytes behind them — timers, home assistant, schedules — render JSON and
  markdown and can hold no image file.
- An image is refused above a configured ceiling rather than downscaled. Re-encoding would need an
  image codec in Domain and would silently change what the model is looking at.
- A miss is cheap and says so. Out of depth, expired, or a host with no store at all, the model
  gets a placeholder naming the path and telling it to read the file again — honest in a way a
  lost attachment's placeholder cannot be, because the file is still on the mount.
- Injected messages do not count toward hydration's distance, for the same reason tool calls and
  results do not: a model that reads twenty images would otherwise age out the photo a person
  actually sent.
- Truncation sees the injected images, since it runs after this pass, and counts each at the flat
  per-image estimate. What it cannot see is bytes: nothing caps how many images one batch of
  parallel reads may show, so a large batch passes the token budget and can still fail as an
  oversized request. Accepted deliberately — the model asked for those files, and the per-image
  ceiling is the bound.
- The context-truncation metric reports the last user message's sender. It now skips injected
  messages, so it keeps naming the person rather than the system.
