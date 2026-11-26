---
paths:
  - "McpServerWebSearch/Handoff/**"
  - "McpServerWebSearch/McpTools/McpWebLogin*"
  - "McpServerWebSearch/McpTools/McpWebBrowseTool.cs"
  - "McpServerWebSearch/Modules/ConfigModule.cs"
  - "McpServerWebSearch/Settings/McpSettings.cs"
  - "McpServerWebSearch/appsettings*"
  - "Domain/Contracts/IWebBrowser.cs"
  - "Domain/Tools/Web/AsMe*"
  - "Domain/Tools/Web/AsPerson*"
  - "Domain/Tools/Web/Login*"
  - "Domain/Tools/Web/Person*"
  - "Domain/Tools/Web/RegistrableDomain.cs"
  - "Domain/Tools/Web/StorageStateFilter.cs"
  - "Domain/Tools/Web/WebLoginTool.cs"
  - "Domain/Tools/Web/WebBrowseTool.cs"
  - "Domain/Tools/Web/public_suffix_list.dat"
  - "Infrastructure/Clients/Browser/*Handoff*"
  - "Infrastructure/Clients/Browser/*Person*"
  - "Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs"
  - "Infrastructure/Clients/Browser/BrowserSessionManager.cs"
  - "Infrastructure/Clients/Browser/AccessibilitySnapshotService.cs"
---

# Web Browsing: acting as a person

See also: `web-browsing.md` (tools, overlays, tabs), `web-images.md` (page images), `camoufox.md` (the browser container).

## Browsing as a person

Logins are handed off, never typed by the model: the person logs in on a live view of a tab in their own browser context and the agent inherits the session (design: `.scratch/web-identity/spec.md`).

**A person is their account, resolved and never claimed.** `PersonResolution.Of` (`Domain/Tools/Web`) reads `Principal.Account.UserId`, which keys the person's browser context, live-only logins, sealed record and logins page. The account is the verification (chat client, Telegram, recognised voice — `.claude/rules/chat-hub.md`, `.claude/rules/telegram.md`, `.claude/rules/voice.md`), so there is no list of verified channels and every channel of one account reaches the same logins. A channel's own name for someone is never looked up. A principal with no account is nobody, and `asMe`, `web_login` and `web_logins` refuse it by name (the voice guest is told their voice was not recognised). Nothing falls back to the anonymous context or to another person. A pre-account record is `HouseholdMigration/Steps/RememberedLogins.cs`'s to re-seal, never read as a fallback.

**A principal is only as true as the channel that stamped it.** A turn's `ConversationContext.Principal` is vouched for by its channel (`DeliveryTargetResolver.BuildConversationContext`), on a server by the call token (`CallTurn`); a context without one is nobody. A schedule's, watch's or login wake's fire acts for the person whose turn last wrote the object, on the delegation the agent signed and checks as the fire arrives (`.claude/rules/delegations.md`, "Deferred turns") — never on a schedule JSON's `userId` or a server-stamped principal; a download's fire, or one whose delegation does not hold, is nobody. A login wake's delegation is the one `web_login`'s call carried (`LiveHandoff.Delegation`). A fire's `Origin.ChannelId` is only where it delivers and its `UserId` only what its author wrote: neither is read.

**The person is asked before the agent acts as them on a site.** `asMe` is a flag the model sets per call, so a page's injected text can ask for it.
- Agent-side, `ToolApprovalChatClient` puts any `asMe: true` call to the person, whatever the whitelist or a remembered tool says, with reason `as_person` (`ExecScreenCodes.AsPerson`). The site is the url's registrable domain, so neither path nor subdomain counts. A plain approve (or a refusal) answers every call of that tool on that site for the rest of the turn, calls of one message included, which wait on the first one's answer rather than each showing a card (`ToolApprovalChatClient._turnAnswers`, keyed by the turn's history as `WrittenOrder` is; a worker's request is a turn of its own). Approve-and-remember covers that tool on that site (`AsPersonSubject`) for the conversation, never the tool itself, and is kept apart from the tool-level set the exec bridge reads. A call with no parseable url is asked every time.
- A schedule's, watch's or download's fire is not asked and runs as the whitelist says; a login wake is asked like the person's own turn. Refs carry no flag, so `web_action` on an `asMe` tab is not asked again.
- **Server-side, `asMe` reaches only a site the person holds a login for** (`AsMeGate` over `ILoginSites`, answered by `PersonLogins.HasLoginAsync`: a remembered or live-only login whose site or any of its `Domains` covers the url's domain); any other is refused before anything opens. That bounds a fire, which nothing asks.
- Still open: a schedule written during the person's own turn runs as them later without asking; the approval of the schedule's write is the gate there.

**Contexts, not fingerprints, separate people.** The anonymous context (every browse without `asMe`) and one person context per person (`PlaywrightWebBrowser.ContextForAsync`, restored from remembered state). A tab belongs to its context for life (`BrowserTab.Context`); reuse and address matching stay within it, and a popup joins its opener's. Refs act as the person exactly when the ref's tab is theirs, which is how a worker reaches its parent's `asMe` tabs; the browse envelope says `asMe` (`WebBrowseTool`). A reconnect closes every context; person contexts come back lazily.

**The handoff tab is outside every pool.** `web_login` opens its url through `IHandoffBrowser` (`PlaywrightHandoffTabs`): no ref, browse or action reaches it while the person types.
- Refused on any turn with an `OriginKind` (schedule, watch, download, the handoff's own turns) and withheld from workers agent-side (`AgentSpec.WithheldTools`, with `web_logins`), because a worker's calls carry its parent's context.
- Who may be handed a link is the Domain tools' (`WebLoginTool`, `WebLoginsTool`); the tab, link and view are the server's (`LoginHandoffs`, `LoginsPages`); `IHandoffBrowser`, `IPersonContexts` and `IPersonStateStore` live in Infrastructure, since no Domain code consumes them.
- The link (`OneTimeLinks`, single use) is `Handoff:PublicBaseUrl` + `/browse/<household>/handoff/<token>` (`BrowsePagePath`, the household of the account the call acts for). Its route ends at the pages listener (8081, `PagesListener`), **which serves nothing else**: `.claude/rules/tunnel-forwarding.md` ("Browse pages"), `.claude/rules/mcp-hosting.md`.
- **The link's GET only shows the page** (previewers prefetch); the page's `POST …/claim` spends the token atomically and binds an HttpOnly, SameSite=Strict cookie (`Secure` when Caddy's `X-Forwarded-Proto` says https) on `Path=/browse/<household>` — no other household's server is sent it. Everything after — frames, input, `state`, Done — answers to that cookie, and the page makes every request under its own household's path.
- A claim for a tab this process no longer holds refuses; a Done whose tab was lost answers 410 and wakes nobody.
- `Handoff:TicketMinutes` (claim) and `Handoff:ViewMinutes` (view); either running out closes the tab and emits nothing.
- **A handoff's hold on the person's logins is one object**: a `HandoffLease` from `PersonLogins.BeginHandoffAsync`, settled once by every exit; "a handoff is open" means an unsettled lease — never add a counter to pair by hand.

**The live view is a screencast, drawn at the view's own size.**
- Frames are `page.Screencast` JPEGs sent as **binary** WebSocket messages, newest only; `fields` and input travel as JSON text (`HandoffEndpoints` ↔ `HandoffPage`). Camoufox is patched not to wait for a per-frame ack the .NET client never sends (`DockerCompose/camoufox/patch-screencast-ack.js`); test fixtures reuse an existing `camoufox:latest`, so rebuild it after the patch changes.
- **The view says its size** (`HandoffInput.Resize`, CSS pixels, on connect and when its width changes — never for a keyboard that only shortens it, or the site reflows under the field); the tab and its popups are drawn at it, and with none asked keep 1920x1080.
- A scrolled page's frames are screenshots (`HandoffStream.ShowAsync`), since Camoufox's screencast ignores scroll. One view at a time: a newer stream ends the older.
- The page on top reports its text fields as a `fields` message (`FieldsScript`: rect and keyboard kind, **never a value**). Input returns as JSON at fractions of the frame; a field tap raises the keyboard with `type=password` so the phone's keyboard learns nothing.
- The page is a served string (`HandoffPage`): WebChat's palette, no typed glyphs.
- **A passkey cannot be used through it**, by design — the container has no authenticator. `DockerCompose/camoufox/server.js` turns off Firefox's USB transport so an ask is refused at once and the site offers its other ways (`LoginHandoffBrowserTests`; rebuild the image after changing it). The view says so in a standing line rather than detecting the ask, which would take `main_world_eval` and a `navigator.credentials` override a login page's bot check can see.

**A dropped view is not an ended login, and the page always has a way on.**
- The page asks `GET …/handoff/state` (by the view cookie: `open`, the site, `canRemember`, `from`) and reconnects while the login is open — on return, on `online`, with backoff; it ends only on 401 or `open: false`.
- The link's GET serves the page again to the browser that claimed it while its login is open, so a reload is not "this link has been used".
- The claim and `state` carry `from` (`chat` — chat client and a voice login's link — `telegram`, or `elsewhere`), and the page words its way back by it; the spent-link, logins and relay offline pages link to the chat (`/`).
- Reconnecting resends the last size unless the width changed.
- The chat client remembers the conversation on screen (`OpenTopicEffect`, sessionStorage) and reopens it on reload, so coming back lands where the reply arrives.
- A change to the handoff script changes its pinned hash: the last release's stays in `BrowsePagePolicy.ScriptHashes` for one release (`PreviousHandoffScript`).

**Done wakes the conversation that asked.** The browse server is a channel server too (`BufferAlways`, subscriber `channel-websearch`, no outbound surface), dialled as the `websearch` channel endpoint. Done emits one `LoginWake`: the asking context's conversation, agent and user, `ReplyTo` its origin target (a satellite's address included), origin `Login`; it queues behind a running turn of a live group, else delivers to the reply target.

**A voice login's link goes to the person's chat client; its answer comes back on the satellite.** `web_login` on a `voice`-origin turn also emits `LoginWake.LinkInChatClient`: reply target `signalr` with no conversation (SignalR mints one, owned by the fire's principal's account via `ConversationFactory`), the voice turn's own principal, user and sender the account's user id, origin `Login`. The tool's answer carries no link. The chat client opens only behind the account's own sign-in, which is what makes a link safe there. The voice guest is refused before anything opens. The wake Done raises keeps the voice turn's reply target, satellite address included, so a live satellite session speaks the answer and, with none, it is bound as an announcement on that satellite alone (`create_conversation`, `.claude/rules/channels.md`).

**Remembered state is sealed, and a save is an allowlist.**
- `RedisPersonStateStore` keeps one record per person (`websearch:person:{userId}`), sealed by Domain's `SealingKey` (`Domain/Security/Sealing/`) under `Logins:StateKey` (`LOGINS__STATEKEY`, base64 of 32 bytes; empty is remembering off, malformed fails startup). The user id is the associated data, so a record copied under another account's key does not open. Keep the layout (AES-GCM, version byte 2): existing records and the tenant kit's move's copies must still open.
- A record it cannot open throws `PersonStateUnreadableException`, and every `asMe` and `web_login` for that person refuses as `unsupported_operation` (`BrowseStatus.LoginsUnreadable`) rather than browsing logged out.
- **A login is a record, not a domain** (`SiteLogin`): `Site` is the registrable domain of `web_login`'s url, `Domains` every domain the handoff touched (`PersonLogins`). A domain is `RegistrableDomain` by the vendored `Domain/Tools/Web/public_suffix_list.dat` (ICANN and private sections, refreshed by hand), so sites on a shared host such as github.io stay apart.
- **Every save writes only what a remembered login covers** (`StorageStateFilter.Only`): an unticked login's cookies, and a third party's, never reach the disk. A domain a remembered and a live-only login share (a provider both sign in through) **is** written, because the remembered one needs it.
- Done ticked adds or replaces the site's remembered login (domains merged) and drops its live-only one; unticked removes a remembered login of that site and makes it live-only; a handoff that runs out is live-only unless its site is already remembered; one that touched nothing is no login. None touches another login (`PersonLogins`).
- Live-only logins are held in memory only (`PersonLogins`), forgotten on `IPersonContexts.ContextsClosed`.
- A full export happens only when a login is kept or forgotten; the save after use runs `Logins:SaveDebounceSeconds` after the person's first request since the last save — never pushed off by steady traffic, never while a lease is open — and exports cookies alone (`IPersonContexts.ExportCookiesAsync`), laid over the stored origins; a record past `Logins:MaxRecordBytes` drops IndexedDB, then localStorage, never cookies.
- No model tool reads or writes the store.

**A person sees and revokes their logins on a page; the model can only hand them it.** `web_logins` mints a `/browse/<household>/logins/<token>` link under the handoff's rules, bound to the person when minted (`LoginsPages`), so nothing the page sends can name somebody else. Both links keep their own link-claimed cookies rather than the accounts session, so a Telegram-only person opens either from their phone. Forget (`PersonLogins`, `IPersonContexts.ReplaceStateAsync`) strips only the domains no remaining login (remembered or live-only) still covers, from the live context (rebuilt from its state, closing the person's tabs: the only way to clear localStorage without a per-origin API) and the record, so forgetting a shop leaves a shared Google session; it is refused while a lease is open. No model tool lists or forgets: a page must not be able to talk the agent into revoking a session or covering its tracks.

**Password values never leave a snapshot.** The accessibility snapshot reports a filled password field as `AccessibilitySnapshotService.MaskedPassword` and an empty one as empty, in every context — whoever typed it.
