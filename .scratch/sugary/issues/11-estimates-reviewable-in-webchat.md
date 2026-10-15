# 11 — Estimates reviewable in WebChat

**What to build:** Each conversation the Sugary channel opens also appears as a WebChat topic under the `sugary` agent, showing her message with its photos (and clip, once filming exists) and the agent's reply as sent; a structured reply shows as its JSON text. This extends the fan-out that today serves only agent-initiated messages to messages a channel sends on a person's behalf when the channel asks for a review copy, with the Sugary channel kept as the primary target. See the spec's "WebChat review".

**Blocked by:** 05

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] The topic is minted on the conversation's first turn and later turns attach to it; the Sugary channel stays first, so the app never waits on WebChat.
- [x] Decide and record how WebChat shows attachments whose bytes rest in the Sugary channel's store (WebChat shows only its own uploads today); the photos are visible in the topic.
- [x] The topics follow the normal topic retention.
- [x] Tests at the chat monitor's delivery resolution, beside the existing conversation-context tests.
- [x] If the widened fan-out warrants it, an ADR records it.

## Comments

**Built (2026-10-15).** Every estimate turn now also shows as a WebChat topic under `sugary`: her
message (the turn's text) with its photos, and the reply as sent (the JSON as text). ADR:
`docs/adr/0046-a-channel-may-ask-for-a-review-copy-and-webchat-shows-the-conversation-it-anchors.md`;
`.claude/rules/channels.md` got a paragraph and `CONTEXT.md` the term **Review copy**.

1. **The ask.** `ChannelMessageNotification.ReviewCopy` (`ReviewCopy { Title }`, copied onto
   `ChannelMessage` by `McpChannelConnection`). Generic: any channel may set it. It does not say where
   the copy goes; the deployment declares one review host per endpoint (`channelEndpoints[]
   .hostsReviewCopies`, `true` on `signalr` only, surfaced as `IChannelConnection.HostsReviewCopies`
   like `AttachOnly`; `AgentAppSettingsTests.ReviewCopies_AreHostedByWebChatAlone`).
2. **The fan-out.** `DeliveryTargetResolver`: a message with no `ReplyTo` that asks for a review copy
   is resolved as the existing fan-out over `[channel on its own conversation, host with none]`. The
   channel's conversation anchors and stays targets[0], so the agent, the history, approvals and the
   turn's context all name Sugary's conversation; the host gets `create_conversation` with that id
   as `existingConversationId` and the title as topic name. A host that is down or throws is skipped
   and the turn goes on. It is resolved again on every turn (no `ReplyTo` for the group to reuse),
   so later turns and new groups attach to the same conversation. `ReplyTo`, when set, wins.
3. **Mint on the first turn, attach after: SignalR's half.** `create_conversation` with an existing
   id **and** a non-empty topic name now means "a later fan-out target: show this conversation". If no
   session and no persisted topic holds it and the id parses as a `ConversationIdentity`, a topic is
   minted over that very conversation (`IConversationFactory.CreateOverAsync`: the topic's
   `ChatId`/`ThreadId` are the conversation's, so the topic reads the history the agent writes there),
   with a session, a stream seeded with the prompt and `OnTopicChanged`. Once a topic holds it, the
   call is the turn-start announce it always was (user bubble, `OnStreamStarted`), or persistence alone
   with no session (ADR 0035). An empty name (the announce) behaves exactly as before.
4. **Sugary asks on every estimate turn.** `SugaryChannel.AskAsync` takes a `ReviewCopy?`;
   `EstimateEndpoints` names it `Meal: <her words>` (cut at a word after 48 characters, with an
   ellipsis) or `Meal photo` with no words. Conversation ids are now minted with
   `ConversationIdGenerator` (`chatId:threadId`, was a `Guid`), and the upload store files a
   conversation under `AttachmentEndpointPaths.ConversationDirectory` (`chatId-threadId`), so photo
   ids look like `123-456/<guid>`.

**The attachment-display decision: copy into WebChat's store (ticket 05's option 1).** When the
review host was resolved and the message has attachments, the resolver starts (never awaits) a copy:
each file is fetched from the channel that holds it (`FetchAttachmentAsync`) and handed to the host
with its reference (`IChannelConnection.StoreAttachmentAsync` → `store_attachment`, a new
channel-protocol tool, hidden from the model in `ThreadSession`). SignalR's `StoreAttachmentTool`
keeps it under **the same id** in its upload store (`AttachmentStore.SaveCopyAsync`), in the topic's
space, under the same type and size rules as an upload, and refuses an id it already holds, so
nobody's upload can be written over and nothing can be named outside the store. The shared history's
references then resolve through WebChat's own ticketed `/api/attachments/{id}` downloads and
`MessageAttachments.razor` renders them as for any upload. Why this one:
- **Option 2 (a Sugary download route the browser reaches)**: a second origin for the client, CORS,
  HTTPS on Sugary (ticket 12) and a ticket flow spanning two servers. Most machinery.
- **Option 3 (SignalR proxies unknown references)**: SignalR would need its own MCP client of Sugary,
  a channel-endpoint map duplicating the agent's, the secret to dial with, and a channel on the
  history wire; it could not apply its space check to a file it holds no record of, and the photos
  would vanish whenever Sugary was down.
- **Option 1** reused nearly everything: the agent is the one party connected to both and already
  fetches these photos for hydration; WebChat's store, tickets, space check, sweep and topic-delete
  then govern the copy. Cost: a second copy (~0.3–0.5 MB a photo) and the bytes crossing MCP twice
  more, in the background. ADR-0020 holds where it applies: messages and history carry references
  only, and bytes move only through the two attachment tools. The Sugary store is not exposed: only
  the agent reaches it, over MCP behind the deployment secret.

**Retention.** The review topic is an ordinary topic: stamped on every append (the history key parses
as `chatId:threadId`), purged at `purgeHorizon`. Its photo copies are filed under the topic's
conversation directory, so deleting the topic removes them; otherwise SignalR's sweep takes them after
`attachmentRetention` once the conversation is gone. Sugary's own photos keep their 365 days.

**Tests.**
- At the monitor's delivery resolution, `Tests/Unit/Domain/Monitor/ChatMonitorReviewCopyTests.cs`:
  a turn asking for a review copy attaches the host with the channel's own conversation id, the title,
  sender and prompt, while the agent, the approval handler and the context name the channel (channel
  first) and both get the reply; every later turn attaches to the same conversation again; a turn not
  asking gets nothing; a review turn's photo is copied to the host under its own reference with the
  bytes fetched from the channel; a non-review turn copies nothing. `DeliveryTargetResolverTests`:
  a review from the host itself is not doubled, no host means the channel alone, a host that throws or
  is not connected leaves the channel as the only target.
- **"Mints on the first turn, attaches afterwards" is pinned in two halves**: the resolver's part
  (every turn asks the host with the anchor's id) in `ChatMonitorReviewCopyTests`, and the mint/attach
  decision, which is SignalR's, in `Tests/Unit/McpChannelSignalR/CreateConversationToolTests.cs`
  (first call mints a topic over `11:22` with that chat/thread, a session, a seeded stream and
  `OnTopicChanged`; the second attaches with the user bubble and `OnStreamStarted`; after a restart a
  held topic is not minted twice; an id no topic can spell mints nothing).
- Attachment boundary: `StoreAttachmentToolTests` (kept under its id and downloadable from the default
  space; kept in the topic's own space; no overwrite; traversal-shaped ids refused before the disk is
  touched; a file the chat would refuse is refused) and
  `Tests/Integration/Channels/McpChannelConnectionStoreAttachmentTests.cs` (the agent's real
  `McpChannelConnection` over MCP into the real tool, then `MintDownload`; a refusal reads as not
  kept; a channel without the tool keeps nothing). Not connected → false
  (`McpChannelConnectionNotConnectedTests`). Wire mapping in `McpChannelConnectionParsingTests`.
- Sugary: `SugaryEstimateApiTests` — the turn carries the review copy and a conversation id that parses
  as a `ConversationIdentity`; the title from her words, cut, or `Meal photo`; photo ids filed under
  the conversation's WebChat directory.
- Full suite (`Category!=Llm&Category!=Eval&Category!=E2E&Category!=EvalCalibration`): 6,543 passed,
  17 failed, 16 skipped. The 17 were `PlaywrightWebBrowserTests`, `BrowserJQueryWidgetCompatTests` and
  `QBittorrentDownloadClientTests`, failing on Docker (a testcontainers ryuk name conflict, a Camoufox
  container that never started) while other agents ran suites; rerun alone, all 62 pass.

**Decisions and caveats to know about.**
- **A channel asking for a review copy must spell its conversation ids as `chatId:threadId`**,
  otherwise no topic can read the history; SignalR then attaches as before and shows nothing.
- **The empty topic name on the turn-start announce is now load-bearing** (commented at
  `AnnounceTurnStartAsync`): given a name, SignalR would read the announce as a fan-out target and mint.
- **"The app never waits on WebChat" holds for a host that is down or throws** (skipped), and the photo
  copy never blocks. A host that is merely slow is still waited on per reply chunk, because
  `ReplyDispatcher` sends each chunk to all targets together. That is the existing fan-out's behaviour,
  unchanged.
- **An operator typing in a review topic** sends a WebChat turn into the same conversation and group
  as the app's; it is answered in prose to WebChat only, and a later follow-up from the app (ticket
  06) sees it in the history. Nothing stops it.
- **A host down on the first turn** means no topic until a later turn of the same conversation reaches
  it, so a one-turn estimate then has none.
- **Clips (ticket 10):** WebChat's store refuses `video/` types, so a clip is not copied and its review
  shows a placeholder until the store's allowed types and the client learn to show video. Stills are
  images and are copied.
- `IChannelConnection` gained two members (`HostsReviewCopies`, `StoreAttachmentAsync`); the
  not-connected comment in `IMcpChannelConnection` lists the new one.

**Not verified.** Nothing was rendered in a real browser: that the photos show rests on the copy
being downloadable through `MintDownload` (tested) and the existing `MessageAttachments.razor` path
for history attachments. The full-stack check is ticket 12's E2E.


**Merged with tickets 06 and 07 (2026-10-15).** Built beside them from ticket 05, so the merge decided
three things. `OpenEstimates.Open` now mints the `chatId:threadId` id, so the ids the follow-up window
knows are the ones WebChat can show; the app carries it back as an opaque form field (`Sugary.Client`
unchanged). Her answers ask for a review copy too, under the first estimate's title, which
`OpenEstimates` keeps beside the window because an answer has no words of the meal; without it the
host would never get the answer's turn. A nutrition label asks as well (`Nutrition label`), and its
id is minted the same way, since a GUID would have asked for a review SignalR could not show. Pinned
by `HerAnswers_AskForTheSameReviewCopy_OnTheConversationTheAppCarriedBack` and
`ALabelTurn_AsksForAReviewCopy_OnAConversationWebChatCanShow`.


**The review plays the clip and leaves its stills out (2026-10-15).** On the live deployment a
filmed meal's topic showed the eight stills and a placeholder for the clip, read as "it sent both
the video and the pictures". The model got only the clip. The topic now shows what a video model
saw: her photos and the clip, playable, and no stills. ADR 0046 has an amendment.
- **Agent:** the review copy skips the stills a clip on the same message names: they are not
  fetched and not copied (`ClipStills.Without`, now shared with `ClipSelection` and the client).
- **WebChat's store:** `store_attachment` keeps `video/mp4` (`AttachmentSettings.ReviewCopyMediaTypes`,
  all Sugary sends after normalising) up to `MaxReviewCopyClipBytes`, 20 MiB. A person's upload
  still refuses video: `AttachmentLimits` (composer and upload endpoint) is unchanged.
- **Why 20 MiB:** a copy is one MCP call with the bytes base64 in the body, and SignalR's Kestrel
  caps a request body at 30,000,000 bytes by default. 20 MiB is 27,962,028 bytes of base64. A 20 s
  clip at 540 lines, CRF 28, is a few MB. A larger clip is refused and logged, and shows as a chip;
  nothing is chunked and Kestrel's limit is not raised.
- **The download route** answers byte ranges (`enableRangeProcessing: true`) with the stored type, so
  a `<video>` can start and seek; a ticket is reusable until it expires (15 min), so every range
  request is answered.
- **The client:** `MessageAttachments` plays a video reference as `<video controls playsinline
  preload="metadata">` on the minted download URL, re-mints on an error like a thumbnail, and leaves
  out the stills a clip names (`AttachmentDisplay`). A clip it cannot fetch (too large, or a topic
  made before this) shows as a chip. Pictures no clip names show as before.
- **Older review topics** keep their copied stills in the store but no longer show them, and their
  clip, never copied, shows as a chip.

Tests: `StoreAttachmentToolTests` (a clip above an upload's size kept, one over its cap refused,
`video/quicktime` refused); `AttachmentEndpointTests` (an upload of `video/mp4` still 415; a ranged
download answers 206 with `video/mp4` and the range); `McpChannelConnectionStoreAttachmentTests` (a
clip of exactly 20 MiB crosses the agent's real connection over Kestrel and is kept, one byte more is
refused); `ChatMonitorReviewCopyTests` (a filmed meal copies her photo and the clip, and neither
fetches nor copies a still); `AttachmentDisplayTests` (a clip is shown as a clip and its stills not;
pictures no clip names show as before; thumbnails and clips are the ones previewed). There is no
bUnit in the suite, so the component's choice lives in `AttachmentDisplay` and is tested there, as
`AttachmentPreviewCache` is. Nothing was played in a real browser.
