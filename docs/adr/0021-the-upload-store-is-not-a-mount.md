# 0021 — The upload store is not a mount

Status: accepted
Date: 2026-08-09

## Context

Every filesystem in this system is a mount. A server publishes a `filesystem://` resource,
`McpFileSystemDiscovery` finds it at session start, and `VirtualFileSystemRegistry` mounts it;
the model then sees it in the filesystem prompt and reaches it with the `Vfs*` tools. Six
mounts work this way today.

The upload store holds attachment bytes on the SignalR channel server between a person sending
them and the agent reading them. Making it a mount is the path of least resistance: the agent
would blob-read the bytes through the registry, and copying a file into an agent's sandbox
would be `Transfer`, the machinery that already exists for exactly that.

Two things argue against it. Discovery runs over each agent's `mcpServerEndpoints`, which is a
separate list from `channelEndpoints`, so every agent would have to name the SignalR channel
server twice and open a second MCP client to it. More importantly, one upload store serves
every conversation, every user and every space. A mount is visible to the model, and a visible
mount can be globbed: an agent in one conversation could list and read the files someone
attached in another, in another space, belonging to someone else. The mount abstraction has no
per-conversation scope to express, and inventing one — a mount that hides most of itself from
the caller — would contradict what a mount means here.

## Decision

**The upload store is reached only through a channel-protocol tool, hidden from the model, and
is never mounted.**

The agent asks the SignalR channel server for an attachment's bytes by naming its reference,
the same way it asks that server to send a reply. Channel servers already hide their protocol
tools from the model, so this needs no new concept.

Where an agent has a sandbox, it writes the bytes there itself with a blob write, into
`~/uploads/<conversation>/<message-id>/<filename>`, and the message names that virtual path so
the model can act on the file. **The sandbox is the only filesystem where an attachment appears
as a file to the model.** An agent with no sandbox still receives the attachment as model
context and simply has no file to point at.

## Consequences

- The copy into the sandbox is a read plus a blob write rather than a `Transfer`. That is
  duplicated effort of a kind this repo usually refuses, and it is accepted here: reusing
  `Transfer` would have required both ends to be mounts, which is the thing being avoided.
- What an attachment can be used for depends on which agent received it. An agent with a
  sandbox can run code against the file; one without can only look at it through the model. The
  difference is deliberate and follows the agent's configured servers, so it needs no setting
  of its own.
- Attachments are invisible to `VfsGlobFiles` and friends until they land in a sandbox. Nobody
  should add an `uploads` mount later to "fix" that: it would hand every conversation a read
  over every other conversation's files.
- The channel server holds bytes it does not otherwise deal in, so its retention sweep is the
  only thing standing between attachments and unbounded disk growth. It is not an optional
  extra to be added in a follow-up.
