# 0022 — A channel whose transport keeps files has no upload store

Status: accepted
Date: 2026-08-10

## Context

Telegram is gaining attachments: a person sends a photo or a PDF to a bot and the model reads it.
The path of least resistance is to copy what SignalR does — download the file on receipt into an
upload store on the channel server, hand back an `AttachmentReference` naming it, and sweep the
store on a retention window. Everything above the channel already works that way and needs no
change: the reference travels the protocol, the history keeps it and never the bytes, hydration
puts them back on the way to the model, and `fetch_attachment` is how the agent asks.

SignalR needs a store because a browser hands over bytes once and nobody else keeps them. Telegram
does not have that problem. Its `file_id` is a downloadable handle the bot can use for as long as
the bot exists, so the bytes are already held, already durable, and already addressed by a string
that fits an `AttachmentReference.Id` unchanged.

Three facts shape what follows. A `file_id` is per-bot, and `BotRegistry` holds one bot per agent,
so the reference has to name which bot can fetch it. `getFile` refuses above 20 MB, which is below
the 25 MB SignalR allows. And the size is on the update before anything is downloaded, so a file
too large to fetch can be refused while the person is still standing there.

## Decision

**The Telegram channel stores nothing. `fetch_attachment` resolves the bot from the reference and
does `getFile` plus a download, every time it is asked.**

An `AttachmentReference.Id` is `<agentId>/<file_id>`. There is no storage path, no volume, no
sweeper, no retention setting and no `AttachmentSettings` on this channel.

An upload store is therefore one channel's answer to where bytes rest, not something being a
channel requires.

## Considered options

**Copy SignalR's upload store.** Uniform across channels, and a fetch becomes a local disk read.
Rejected because it holds a second copy of a file Telegram keeps anyway, and buys a sweeper, a
volume, a retention setting and a storage path to serve it. It also introduces a failure the
reference-only design does not have: a swept file hydrates to a placeholder while the original is
still sitting in the chat the person can scroll back to.

**Download on receipt into memory, cached in the channel server.** Half a store, with the same
duplicate and an eviction policy to argue about, and it still loses everything on a restart.

**Cache downloaded bytes on disk.** The rejected store arriving by the back door.

## Consequences

- A fetch is an internet round trip on the critical path of a turn, where SignalR's is a disk
  read. `ChannelAttachmentSource`'s 16-entry cache in the agent is the only cache there is, and
  hydration re-fetches every in-window attachment each turn it misses. **The escape hatch, if this
  proves slow, is a bounded in-memory cache in the Telegram channel server** — additive, changing
  no contract, and deliberately not built before there is a measurement.
- Availability moves from receipt time to hydration time. If Telegram is unreachable when a turn
  runs, an attachment hydrates to a placeholder even though nothing was ever lost. In exchange,
  nothing expires: a reference stays fetchable for as long as the bot exists, so the placeholder
  path that retention makes routine on SignalR is here only ever a transient failure.
- 20 MB is Telegram's ceiling and not ours, and it is enforced at receipt with a reply naming the
  limit rather than at hydration with a placeholder the person never sees.
- The reference names the agent, so a fetch works from a cold start with no chat mapping to
  consult. It also means a `file_id` obtained by one agent's bot cannot be fetched by another's,
  which is the correct scope and comes free.
- `CONTEXT.md`'s **Upload store** entry stopped being a description of every channel and became a
  description of one, which is why it no longer promises that everything in it is eventually
  swept.
