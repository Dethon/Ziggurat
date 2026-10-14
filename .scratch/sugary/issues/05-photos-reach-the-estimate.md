# 05 — Photos reach the estimate

**What to build:** She photographs a meal (one or several photos of the same plate, with or without words) and the `sugary` agent's model sees the photos. The Sugary channel keeps the bytes in an upload store of its own, sends attachment references on the turn, and answers the agent's attachment fetch, as the SignalR channel does. See the spec's "The channel role".

**Blocked by:** 04

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] One photo and several photos of the same meal both reach the model, hydrated on the way out like any attachment.
- [x] The history keeps references, never bytes.
- [x] A photo too large for the model is refused before it is sent, with a reason the app shows.
- [x] A model without image input has the turn refused, never answered as if no photo was sent.
- [x] The upload store's retention outlasts the hydration distance, as ADR-0020 requires.
- [x] Tests at the server's boundary fetch the attachments the way the agent does.

## Comments

**Built (2026-10-15).** A photo (or up to four of one plate, with or without words) is now an
estimate's attachment. `POST /api/estimates` reads the app's multipart form unchanged (`photos`
parts, as `Sugary.Client`'s `api.estimate` sends them: `meal-<n>.jpg`, `image/jpeg`, shrunk on the
phone to 1568 px), so the PWA did not change. In order:

1. Neither photo nor words → 400 "Add a photo or describe the meal." (the 400 photo refusal is gone).
2. `PhotoUpload.Refusal` (`McpServerSugary/App/PhotoUpload.cs`): more than `MaxPhotos` (4), a type
   other than JPEG/PNG/WebP, an empty part, or over `MaxPhotoBytes` (5 MB) → 400 with the reason in
   `detail`, which the app shows as written. Nothing is stored and nothing is emitted.
3. `PhotoUpload.ModelRefusal`: the shared `AttachmentCapability.Refusal` asked about the agent the
   turns are routed to (`SugarySettings.Agent`, `"sugary"`) over the catalogue the agent registers
   on connect (`Mcp.Hosting.RegisterAgentsTool` + `MutableAgentCatalog`, as Telegram does). A model
   that cannot read images → 400 "`<model>` cannot read photos, so this meal cannot be estimated
   from them. Describe it in words instead, or count it by hand." Permissive where the catalogue is
   silent, like every channel.
4. The photos go to `UploadStore` under a freshly minted conversation id, in the order sent; the
   turn's text says "One photo of the meal is attached." / "N photos of the same meal are attached."
   (`MealInput.PhotoCount`, the standalone `UserText` word for word), and `SugaryChannel.AskAsync`
   now takes the references and sets `Attachments` on the notification.
5. The agent fetches each one through `fetch_attachment` (`McpServerSugary/McpTools/
   FetchAttachmentTool.cs`): base64 on a hit and an empty string on a miss, the SignalR contract
   exactly, so hydration turns a swept photo into its placeholder. The tool and `register_agents` are
   channel-protocol tools, hidden from the model by `ThreadSession` even though the sugary agent
   dials this same server for its file tools.

The history keeps references: the turn carries only `AttachmentReference`s (pinned at the server's
boundary: no bytes in the content), and what the agent persists from a channel message is pinned
channel-agnostically by `ChatMonitorAttachmentTests.AMessageWithAttachments_PutsNoBytesOnTheUserTurn`.

**The store** (`McpServerSugary/Channel/UploadStore.cs`, `UploadSweeper.cs`): files on disk at
`Uploads:StoragePath` (`/data/uploads`, in the existing `./volumes/sugary` volume, so compose did
not change). The layout is WebChat's: one directory per conversation, one per attachment, the bytes
beside a `meta.json`. Ids are `<conversation>/<attachment>` and checked segment by segment before
anything touches the disk (a traversal-shaped id reads as nothing; tested). The sweep runs every
6 h and takes anything older than the shared `retention.json`'s `attachmentRetention` (365 days),
which `SugarySettings.Retention` now binds like every host.

**Why disk, not SQLite or Redis.**
- *Not her log's database.* The SQLite file holds what she saved and is what gets backed up with
  the volume. Photos nobody saved would bloat it, deleting rows does not shrink it without a VACUUM,
  and nothing here is ever joined, listed or rendered. ADR-0043's argument for SQLite (relational
  rows read by date and rendered as CSV) does not apply to opaque bytes.
- *Not Redis.* This server has no Redis connection, and adding one for blobs would be a new
  dependency for the one container that deliberately has none. ADR-0020 only asks that the store be
  where the channel can answer a fetch for longer than the hydration distance.
- Disk is what WebChat's upload store already is, and ADR-0021 (the store is not a mount) holds:
  nothing lists it.

**Why age alone, and why 365 days.** WebChat's sweep also waits for the topic to be gone, asking
the agent's thread store. Sugary cannot ask (no Redis), and does not need to. A meal's conversation
is a handful of turns over a few minutes, so its 20-message hydration distance is used up long
before any window, and the shared `attachmentRetention` is at least the `purgeHorizon` (tested). A
photo therefore outlives every conversation that references it, which is also what ticket 11 needs
for the review topic. On disk that is roughly 0.3–0.5 MB per phone photo, so one to two GB a year at
a few photographed meals a day.

**Other decisions to know about.**
- *5 MB, not the standalone's 8 MB.* The agent's patchable models include Haiku 5.5 and Opus 5.5 (the
  label model), and Anthropic's API refuses an image over 5 MB. The app's resized JPEGs are far
  below either. Kestrel's default 30 MB body limit covers 4 × 5 MB, so no limit was widened.
- *An empty photo has its own reason* ("A photo arrived empty. Take it again."). The standalone
  folded it into the size message.
- *The agent id is spelled twice.* The message still names no agent (ticket 04's decision, so
  `agentDefaults` stays the routing). The channel needs to know which catalogue entry to ask about,
  so `SugarySettings.Agent` repeats `agentDefaults.byChannel.sugary`, and
  `AgentAppSettingsTests.TheSugaryServer_AsksAboutTheAgentItsChannelIsRoutedTo` fails if the two
  files ever disagree.
- *The capability refusal is the channel's, and it is the only guard for photos.* ADR-0045 keeps
  image refusals at the channels: the send refuses only clips. Before the agent has registered (a
  cold start), photos go. A blind model would then get a request the provider rejects whole (ADR-0029:
  `404 No endpoints found that support image input`), which reaches the app as a 502 carrying that
  error. Either way the answer is never one made without the photo.
- *`AttachmentCapability.Refusal`'s own sentence is not shown.* It ends "Pick a different model and
  send it again", which she cannot do from the app. Only its verdict is used, and the reason is
  worded for her (the rule's comment invites callers to add their own tail).
- *Status 400 for every photo refusal*, like the other refusals of what was sent. The app shows
  `detail` for anything but 503.
- *No PWA change.* The client's Spanish UI shows the server's English `detail`, as the standalone
  did.

**Tests.**
- `Tests/Integration/McpServerTests/SugaryEstimateApiTests.cs` (real server, the agent played over
  MCP by its own `McpChannelConnection`). It covers:
  - one photo with no words, and three photos with words: references on the turn, the count in the
    text, each fetched through `ChannelAttachmentSource` (the agent's own `IAttachmentSource`, what
    hydration calls) and compared byte for byte;
  - unknown and traversal ids fetched as nothing;
  - over 5 MB, not an image type, empty, and five photos, each refused with its reason, with no
    agent connected (so the refusal is proven to come before the send) and nothing stored;
  - a registered blind model refused before sending, with words alone still going through;
  - photos going through when the model sees, when only another agent's model is blind, and when
    nothing is registered.
- `Tests/Unit/McpServerSugary/UploadStoreTests.cs`: the window is bound from `retention.json` and
  outlives the purge horizon; the sweep takes aged photos, keeps younger ones and ones a day short
  of the window, takes a half-written attachment, and is registered.
- Full suite: 6,547 passed.

**For ticket 10 (filming a meal): the clip and its stills go in this store.** Save the normalised
clip and each still with `UploadStore.SaveAsync` under the **same** conversation id as any overhead
photo. Each is its own attachment, fetched by the same `fetch_attachment`. Then build one
`Attachments` list: the clip's reference with `Stills = [.. stills.Select(s => s.Id)]`, the stills'
references, and the photos (the shape in ticket 09's comment). Things that will need changing:
- `PhotoUpload.Refusal` knows only `photos` parts. A `clip` part needs its own rules (normalised
  length, the 20 s refusal), checked before storing, as photos are now.
- `ModelRefusal` already does the right thing for a clip: `AttachmentCapability.Refusal` refuses a
  `video/` type only for a model that takes neither video nor images. Pass the clip's media type too.
- `MealInput.PhotoCount` should count the photos she took, not the stills. How the text names a clip
  is ticket 10's wording to choose.
- The store's `MaxPhotoBytes` does not apply to a clip. Give it its own ceiling.
- The sweep needs nothing new: a clip and its stills age out together, as they were saved together.

**For ticket 11 (WebChat review): how the photos could show.** The review copy will carry these same
references, but their bytes rest here, in Sugary's store. WebChat's browser fetches attachments from
SignalR's own store through ticketed `/attachments/{id}` downloads, and nothing in SignalR can read
Sugary's disk. This is the open fork:
1. **Copy the bytes into SignalR's store when the review copy is delivered.** Most of this already
   exists: a delivery target's channel would fetch each reference through the agent (which can
   already fetch from Sugary) or be handed the bytes. The cost is that bytes cross MCP a second time.
2. **Give Sugary a download route the WebChat client can reach,** gated like SignalR's (a ticket
   minted over MCP), with WebChat's renderer told which channel holds a reference. The bytes rest in
   one place, but WebChat's client learns about a second origin, and that origin needs the HTTPS
   ticket 12 adds.
3. **Have SignalR proxy unknown references to the owning channel** through the agent's
   `IAttachmentSource`. This needs a reference to carry its channel (it does on the persisted message,
   via `GetAttachmentChannelId`, but not on the wire DTO).

Whichever is picked, the review topic's lifetime (`purgeHorizon`) is already covered by this store's
365-day window. Nothing for 11 was built here.

**Not done.** No real model was called, so whether Gemini 3.8 Flash reads several photos of one
plate as one meal through this path is unmeasured (ticket 08's eval). Ticket 06's follow-up turns
will resend the photos within the hydration distance with no change here, since the references stay
on the first turn's message.
