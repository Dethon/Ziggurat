# 0046 — A channel may ask for a review copy, and WebChat shows the conversation it anchors

Status: accepted
Date: 2026-10-15

## Context

The Sugary app sends every meal estimate as a turn of the `sugary` agent through its own channel
server (`.scratch/sugary/spec.md`). The channel parses the reply and shows nobody the conversation, so
once a meal is saved nobody can read what the model was sent and what it answered. The operator wants
each estimate conversation to also be a WebChat topic under the `sugary` agent, with her photos and
the reply as sent (a structured reply is its JSON text).

A fan-out already existed, for agent-initiated messages only. A schedule or a watch names its reply
targets (`ReplyTo`); `DeliveryTargetResolver` resolves them in order, the first resolved conversation
anchors a shared id, and later targets that need minting attach to that id (voice binds a satellite
to it). The first target is also the group's delivery identity: the agent is built on it, the history
is written under it, approvals go to it and the turn's conversation context names it.

Three facts shaped what an estimate's review needed:

- **The Sugary channel must stay first.** The app waits on its own request for the parsed reply, and
  the turn's context and approvals belong to it. WebChat being down, slow or absent must cost the
  review and nothing else.
- **A WebChat topic reads its history from its own conversation key.** The browser asks for the
  history under the topic's `chatId:threadId`, and the agent writes it under the delivery identity.
  With Sugary first, those are the same only if the topic is made over Sugary's conversation.
- **The photos rest in the Sugary server's upload store**, in its own volume, reached by the agent
  through `fetch_attachment` over MCP behind the deployment secret. WebChat's browser fetches
  attachments only from SignalR's store, through ticketed `/api/attachments/{id}` downloads, and the
  history the two would share names the photos by Sugary's references.

## Decision

**A channel message may ask for a review copy. The review host attaches to the conversation the
channel anchors, as a later target of the same fan-out, and is handed a copy of the message's files
under their own references.**

- **The ask is a field, not a target.** `ChannelMessageNotification.ReviewCopy` (`ReviewCopy`: a
  `Title`) is copied onto the `ChannelMessage`. The channel does not name where the copy goes, as it
  does not name the agent: the deployment declares one review host per endpoint
  (`channelEndpoints[].hostsReviewCopies`, `true` on `signalr` alone, pinned by
  `AgentAppSettingsTests`), surfaced as `IChannelConnection.HostsReviewCopies` like `AttachOnly`.
- **It is the existing fan-out, with the channel first.** For a message with no `ReplyTo` that asks
  for a review copy, the resolver fans out to `[the channel on its own conversation, the host with
  none]`. The channel's conversation anchors; the host is asked `create_conversation` with that id as
  `existingConversationId` and the review's title as the topic name. Nothing else about delivery
  changes: the agent, history, approvals and context name the channel's conversation, every reply
  chunk goes to both, and a host that fails or is not connected is skipped like any target.
- **It is resolved again on every turn**, because the message carries no `ReplyTo` for the group to
  reuse. So each later turn — the answers to the estimate's questions, or a new group after a
  restart — attaches the host to the same conversation and announces the turn there.
- **A later fan-out target with a topic name shows the conversation.** SignalR's `create_conversation`,
  given an existing id *and* a topic name, mints a topic over that very conversation (`TopicMetadata`
  with the conversation's own `ChatId`/`ThreadId`, `IConversationFactory.CreateOverAsync`) when no
  session and no persisted topic holds it, and opens a session and stream on it; once one holds it,
  the call is the turn-start announce it always was, and with no session it is persistence alone
  (ADR 0035). With no topic name (the announce) or an id a topic cannot spell, nothing changes. No
  existing path sent SignalR an existing id with a name: only SignalR and voice mint, and voice is
  ordered last.
- **The anchoring channel must spell its conversations as a topic does** (`ConversationIdentity`,
  `chatId:threadId`), or no topic can read the history. Sugary now mints its ids with
  `ConversationIdGenerator`; Telegram's already are.
- **The files are copied to the host under the same reference** (`store_attachment`, a seventh
  channel-protocol tool, hidden from the model; `IChannelConnection.StoreAttachmentAsync`). When the
  review host was resolved and the message has attachments, the resolver starts — and does not await
  — a copy: each file is fetched from the channel that holds it and handed to the host with its
  reference. SignalR keeps it in its upload store under that exact id, in the topic's space, under
  the same type and size rules as an upload, and refuses an id it already holds, so nothing anyone
  uploaded can be written over. The shared history's references then resolve in WebChat's store,
  and the browser downloads them through the ticketed route it already uses. Sugary files its photos
  under `AttachmentEndpointPaths.ConversationDirectory`, so the copies sit where deleting the topic
  removes its files.
- **Retention is the topic's.** The review topic is an ordinary topic: stamped on every append,
  purged at the `purgeHorizon`. Its copies go with the topic's deletion or, after the shared
  `attachmentRetention`, with SignalR's sweep once the conversation is gone; Sugary's own photos
  keep their 365 days, which outlive the topic.

## Considered options

Ticket 05 left three ways to show photos whose bytes rest in Sugary's store.

**Give Sugary a download route the WebChat client reaches** (a ticket minted over MCP, the client
told which channel holds a reference). The bytes rest once, but the browser learns a second origin,
which needs CORS, HTTPS on Sugary (ticket 12) and a ticket flow spanning two servers. Rejected as the
most machinery for the least gain.

**Have SignalR proxy an unknown reference to the channel that holds it.** Bytes rest once, and the
browser keeps one origin. But SignalR would need an MCP client of its own, a map of channel
endpoints duplicating the agent's, the deployment secret to dial with, and a reference that names
its channel on the history wire. It could not apply its space check either — it holds no record of
a file it does not keep — and a review's photos would vanish whenever Sugary was down. Rejected.

**Copy the bytes into SignalR's store when the review is delivered.** Chosen. Almost all of it
existed: the agent is the one party connected to both channels and already fetches the photos for
hydration; SignalR's store, tickets, space check, sweep and topic-delete already govern what it
holds. The cost is a second copy of each photo (a few hundred KB) and the bytes crossing MCP twice
more, in the background. ADR 0020's rule holds where it applies: the messages and the history carry
references only, and bytes move only through the two attachment tools.

**Make WebChat the first target.** The topic would read its own history, with no topic minted over
another channel's conversation. Rejected: the delivery identity, the approvals and the turn's
context would name WebChat, and a WebChat outage would cost the app its estimate.

**Let the channel set `ReplyTo` itself.** It would have to know the agent's name for WebChat's
endpoint, and the group would reuse its first turn's targets on later turns, so the host would never
announce them. A flag the deployment resolves is smaller.

## Consequences

- An operator typing into a review topic sends a WebChat turn into the same conversation and group
  as the app's; it is answered in prose to WebChat only, and the app's next follow-up turn sees it in
  the history. Nothing stops it, since it may be what an operator wants.
- `IChannelConnection` has two more members. Not connected, `StoreAttachmentAsync` answers false,
  the silent side of ADR 0011's rule: its only caller logs.
- ~~WebChat's store refuses `video/` types, so a clip (ticket 10) is not copied and shows as a
  placeholder until that store and the client learn to show one.~~ Superseded by the amendment
  below: the clip is copied and plays, and its stills are not.
- "The app never waits on WebChat" holds for a host that is down or fails, which is skipped. A host
  that is merely slow is waited on per chunk: `ReplyDispatcher` sends each chunk to every target
  together and moves on when all have answered. That is the existing fan-out's behaviour, unchanged.
- The turn-start announce's empty topic name is now load-bearing: given a name, SignalR would read
  the announce as a later fan-out target and mint a topic.
- A review copy whose host is down on the first turn has no topic until a later turn of the same
  conversation reaches the host; a single-turn estimate then has none.

## Amendment (2026-10-15) — the review plays the clip and leaves its stills out

A filmed meal's review showed the clip's eight stills and a placeholder for the clip, because
WebChat's store refused `video/` and the stills, as ordinary pictures of the message, were copied.
The operator read that as "the model was sent both", and it was not: a video model is sent the
clip and none of its stills (ADR 0045). A review is for reading what the model was sent, so the
topic now shows what a video model saw.

- **The clip is copied; its stills are not.** `DeliveryTargetResolver` copies
  `ClipStills.Without(attachments)`: a still a clip on the same message names is neither fetched
  nor handed to the host. `ClipStills` is the one reading of which pictures are a clip's, shared by
  `ClipSelection` and the client.
- **WebChat keeps a clip as a review copy only.** `AttachmentSettings.ReviewCopyMediaTypes`
  (`video/mp4`, which is all the Sugary channel sends after normalising) are accepted by
  `store_attachment` under a cap of their own, `MaxReviewCopyClipBytes`. A person's upload still
  refuses video: `AttachmentLimits`, which the composer and the upload endpoint both read, is
  unchanged.
- **The cap is what one MCP call can carry.** A copy crosses as one `store_attachment` call with the
  bytes base64 in its body, and SignalR's Kestrel caps a request body at its default 30,000,000
  bytes. 20 MiB is 27,962,028 bytes of base64, so the cap is 20 MiB, pinned at the real boundary
  (the agent's `McpChannelConnection` into the real tool over Kestrel). A 20 s clip at 540 lines,
  CRF 28, runs a few MB. A larger one is refused and logged, and the review shows it as a chip, as
  before; nothing chunks it.
- **The client plays it.** `MessageAttachments` renders a video reference as `<video controls>` on
  the ticketed download URL it already mints for a thumbnail, and leaves out the stills a clip names
  (`AttachmentDisplay`). The download route answers byte ranges (`enableRangeProcessing`), which a
  `<video>` asks for to start and to seek, and the ticket is not spent by a request. A clip that
  cannot be fetched, or was never copied, shows as the chip it was.
- A message with pictures and no clip shows every picture: a still is a still only when a clip
  names it.
- Review topics made before this keep their copied stills in WebChat's store, but no longer show
  them, and their clip shows as a chip: it was never copied.
