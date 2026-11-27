# 06: Images on an ask

Spec: `.scratch/app-asks/spec.md` ("Files").

**What to build:** `apps.ask(text, { files })` sends images from the page, from a camera or a picker.
The apps server stores them in its upload store and names them on the message. The agent fetches them
through `fetch_attachment` on the apps channel, for that household. A follow-up's earlier images come
back from history.

**Blocked by:** 02, 04

**Status:** done

- [x] Images arrive on the message as references, and the model is sent them (origin and agent seams).
- [x] Count, size and type limits are apps-server settings. Anything over them is refused before any turn.
- [x] A reference is served only for the household it was stored for, and never fetched from another household's server.
- [x] A follow-up carries no files and the model still sees the first ask's images.
- [x] The sweeper removes uploads after the retention.
- [x] The volume and settings are wired, and conformance tests updated.

## Comments

**Done (2026-11-26).** What 08-11 build on:

- **The wire** (`Domain/Apps/AppAsks`): an ask with images is `multipart/form-data` of exactly one `ask` field
  (`AppAsks.AskField`), the same JSON an ask without images sends as its body (`AppAskRequest`, strict as
  before), and a `files` part per image (`AppAsks.FilesField`). An ask without images is a JSON body as before.
  Any other field, no `ask`, or a form over its bound is 400 `invalid_argument` naming the shape. The client
  library's `apps.ask(text, { files })` (Files, Blobs or a FileList) builds the form; no new export.
- **Limits** (apps-server settings, held by `AppsShippedSettingsTests`): `Asks:MaxImages` 4,
  `Asks:MaxImageBytes` 5 MiB (5,242,880; the largest image any model an app may ask on takes), and
  `Asks:ImageTypes` (the shipped file names `image/jpeg`, `image/png`, `image/webp`, `image/gif`; **the code's
  default is empty**, as `Cdns` is, because the configuration binder appends to a non-empty default list, so a
  deployment could never narrow it). Each image's type is **sniffed from its bytes** (`AppAskImages.Sniff`;
  a type named beyond the four is never matched), and the sniffed type is the reference's `MediaType`, so the
  page's declared type is never what the model is sent. Too many, empty, too large, or no type taken: 400
  `invalid_argument` naming the limit, **before any turn, before counting, nothing kept**. The route raises the
  body limit to the JSON's bound plus `MaxImages` x `MaxImageBytes` (21 MB at the defaults).
- **Storage**: `AddUploadStore(AskUploads.On(StoragePath), Retention)` (store, sweeper, `fetch_attachment`),
  at `<volume>/.uploads` (`BuiltAppStore.UploadsIn`: a dotted name beside the households, as `.removed`, so no
  household id takes it and nothing new is mounted; `DeploymentConformanceTests` pins it inside the apps
  volume). `AppsSettings.Retention` reads the shared `retention.json`. Images are saved
  `SaveForHouseholdAsync(app.HouseholdId, conversation, ...)` **only after the ask is counted and its
  conversation settled**, in the order sent, and named on the message's `Attachments`. **Nobody listening takes
  them back** (`UploadStore.Remove`, new: deletes each reference's directory and its conversation's once
  empty), beside giving back the count and forgetting the conversation. A timeout, failure or page abort keeps
  them until the sweep.
- **The agent signs every fetch for the turn** (general, not apps-only): `IChannelConnection.FetchAttachmentAsync(id,
  ConversationContext? turn, ct)` sends the turn's context as the call's `_meta`, so `SigningClientTransport`
  signs it as the turn's `Identity`, as any of the turn's calls. Hydration signs as **the turn being sent** (the
  request options' context, threaded through `AttachmentHydration.ApplyAsync` and
  `IAttachmentSource.FetchAsync(channel, household, id, turn)`), never a stored message's, so a follow-up's
  earlier images are fetched for whoever asks now; landing signs as the message's own context; a review copy as
  the admitted identity (`DeliveryTargetResolver.ResolveAsync` gained the identity). `ChannelAttachmentSource`
  keys its cache by the signed household too, so one household's cached bytes are never another's hit.
  **Sugary, Telegram and SignalR are unaffected**: their files are kept for no household (Sugary's
  `SaveAsync`) or their fetch tools read no identity, and a person-signed fetch of a Sugary photo is pinned
  (`SugaryEstimateApiTests`).
- **Deviation**: the spec says a reference is served for its "household and conversation"; it is served for
  the household only (the ticket's wording). Ids are unguessable GUIDs under the conversation's directory.
- **Not checked**: whether the model can see images. The apps server reads no catalogue and the agent does
  not check a third time, so an app on a model without vision sends them anyway. What the page then gets is
  unverified (likely the provider refusing the request, a failed turn, 422 `transient_dependency`); 10/11
  may want `./check` or the skill to say so.
- **Also**: images are accepted on a follow-up too (kept under the same conversation); a timed-out, failed or
  aborted ask's images stay until the sweep; landing (into a declared sandbox) is signed for the turn but has
  no test of its own; the uploads path is derived from `StoragePath`, so there is no storage setting and no
  compose change. The binder appending to a non-empty default list was checked by a throwaway probe
  (`["a","b"]` plus a configured `c` binds `a,b,c`).
- **Tests**: `AppAsksTests` (sniffing whatever the name, limits naming why, none), `HouseholdUploadsTests`
  (remove, a reference shaped to reach outside removes nothing), `ChannelAttachmentSourceTests` (fetched for the
  turn asking, never another household's cache hit), `OpenRouterChatClientAttachmentTests` (every file, an
  earlier message's too, fetched for the turn being sent), `ChatMonitorReviewCopyTests` (the copy fetched for
  the turn's identity), `AppsAsksTests` (a form's images named on the message as their bytes are, fetched over
  the wire by the agent's real connection and call tokens for Her and Him, refused for the neighbour's
  household and for nobody; each limit and form shape refused before any turn, keeping and counting nothing;
  nobody listening keeps none; swept after the retention on a fake clock handed to the store alone),
  `AppTurnAgentTests` (an ask's image fetched on the apps channel for the viewer and sent to the model as a
  picture; a follow-up with none still sends the first ask's, fetched for the viewer, on a real Redis history),
  `AppsServingInABrowserTests` (the Chromium case's schema ask is now a follow-up carrying a `File`),
  `AppsShippedSettingsTests`, `DeploymentConformanceTests`, `SugaryEstimateApiTests`. The `apps` skill is
  unchanged (11 teaches `files`).

