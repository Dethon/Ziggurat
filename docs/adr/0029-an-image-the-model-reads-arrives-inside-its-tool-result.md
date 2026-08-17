# 0029 — An image the model reads arrives inside its tool result

Status: accepted
Date: 2026-08-17

## Context

The filesystem read tool reads text. A mount can hold a screenshot, a scanned page or a diagram,
and the model has no way to look at any of it — the file is either refused or, worse, decoded as
text. Every other way the agent sees an image already works: a person attaches one and hydration
puts the bytes back on their message.

The obvious fix is for the tool to answer with the image. On the Chat Completions wire it cannot:
tool results reach the model as an OpenAI-style `tool` message, whose content is a string — content
parts are, in OpenRouter's own words, "only for the 'user' role". Returning a `DataContent` from
the tool sends a base64 data URI as the tool message's *text*: no provider decodes pixels from
there, and a 500 KB PNG becomes roughly 170k tokens of gibberish.

The Responses API removes the constraint. A `function_call_output` item's `output` accepts an
array of content parts including `input_image` — the same shape OpenAI's own Codex uses for its
`view_image` tool. OpenRouter serves the Responses API (beta) at the same base URL and, verified
live against it with this deployment's key on 2026-08-17:

- an `input_image` inside a `function_call_output` reaches a multimodal model, which reads it;
- `session_id`, `provider` routing and `usage: {include: true}` are honoured, and prompt caching
  works (a repeated 6.3k-token prefix came back 99% cached at a tenth of the cost);
- tool-call round trips work on every configured model, including the translated non-OpenAI ones;
- a request carrying an image to a model without vision is **rejected whole** with
  `404 No endpoints found that support image input` — images are not stripped;
- reasoning arrives as `response.reasoning_text.delta` events, which
  `Microsoft.Extensions.AI.OpenAI` 10.8.3's Responses adapter surfaces as `TextReasoningContent`
  natively, and the same adapter serializes a `FunctionResultContent` whose `Result` is a list of
  `AIContent` into `function_call_output` content parts.

The `OpenAI.Responses` namespace in the OpenAI .NET SDK is still `[Experimental("OPENAI001")]`:
the service API is GA, but the C# surface may reshape between minor versions. The suppression is
explicit in the two csproj files that touch it, and the package is pinned.

What the wire cannot answer either way is where the bytes come from at send time. An attachment's
bytes stay with the channel that took the file — `IAttachmentSource` is fetch-only, keyed by
channel and reference — and a read image has no channel. Re-reading the file from the mount on
every send was considered and rejected: the chat client is built per model from DI while the mount
registry is built per session on `ThreadSession`, so the registry would have to be carried onto
every turn's options, and a 15 MB file on an outpost would then be fetched over MCP from somebody's
laptop once per send for as long as it stayed in view.

## Decision

**The chat client speaks the Responses wire, and an image the model reads travels inside the tool
result that answered the read.**

The tool answers a text envelope naming the path, the media type, the size, and whether the image
was shown. On the way to the model, the same pass that hydrates attachments recognises each
envelope in a tool-result message and rewrites that result into a list of contents: the envelope's
text, then the picture. Nothing else is added to the conversation — no injected message, so
nothing to attribute, place, or exclude from any count.

**Hydration widens rather than splitting.** It is putting bytes back where a reference sits,
whoever put the reference there. One distance rule, one placeholder shape, one pass.

**The bytes rest with the agent.** The tool writes them to a store of the agent's own, keyed by
conversation and tool call, read back by the rewrite. The message distance is the real bound — the
key is deleted once, on the send where the image drops out of view — and a time horizon is the
backstop for a conversation that goes quiet.

**Capability is asked twice, once per end.** The tool asks it when it runs, so a model without
vision gets `shown: false` and nothing is stored. The send asks it again for the model the turn
actually resolved to, because a config patch can move the conversation onto a model without vision
after an envelope was written — and the wire rejects a request carrying an image such a model
cannot take rather than stripping it. That turn gets a placeholder saying the model does not
accept images, and keeps the bytes for the turn that patches back.

### Rejected: the bytes as their own user message

The design this ADR originally recorded, built for the Chat Completions wire: one user message
inserted after the whole tool-result message, attributed to the system, carrying a label and the
bytes per image read in that batch. It worked, but it existed only because that wire's tool
message is a plain string, and it dragged machinery behind it — a placement rule
(`FunctionInvokingChatClient` answers every call of an iteration in one message, and some
providers reject a conversation where a call is unanswered before anything else appears), a
sender attribution, an `IsInjected` flag, and exclusions from both the hydration distance and the
truncation metric's sender. Switching the wire deleted all of it.

## Consequences

- Every agent now runs on OpenRouter's Responses endpoint, which OpenRouter labels beta. The
  reasoning/cost/cache plumbing was re-verified against it live; the risk that remains is
  OpenRouter reshaping the beta, and the E2E suite is the tripwire.
- The reasoning scrape is gone: the Responses adapter surfaces reasoning deltas itself. Cost and
  `cached_tokens` are OpenRouter extensions the typed usage drops, so the transport tee still
  reads them — now from the `response.completed` event, where this wire says them once.
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
- Truncation sees the rewritten results, since it runs after this pass, and estimates each part as
  what it is — the image at the flat per-image estimate, never as base64 text. Nothing caps how
  many images one batch of parallel reads may show, so a large batch passes the token budget and
  can still fail as an oversized request. Accepted deliberately — the model asked for those files,
  and the per-image ceiling is the bound.
