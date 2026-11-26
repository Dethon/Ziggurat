# App asks: a built app asks the agent and gets an answer back

Status: ready-for-agent
Branch: `app-asks`, from `app-spaces`

## Problem Statement

A built app can show the household's data but cannot think about it. Sugary works because a
developer hand-wrote a channel server that sends each meal to the `sugary` agent with a schema and
parses the answer. SexyTime only registers agents and never starts a turn. Anyone who wants an app
that estimates, classifies, plans or explains has to wait for a developer to write such a server.
Jonas can build the page but has no way to put a model behind it.

## Solution

A built app can **ask**. The client library gains `apps.ask(text, options)`. The app sends text and,
optionally, a JSON schema, images and a conversation id to continue. It gets back the answer: parsed
JSON when it sent a schema, otherwise text, plus the conversation id to pass for a follow-up.

The ask runs as a turn for the person viewing the app, answered by the app's own instructions. Those
instructions are a file in the app's repository, run on the building agent's model and the
household's key. The turn may use the tools of the servers the app manifest declares, but only tools
that never ask for approval. A call that would ask is refused at once, and nothing is offered to the
viewer's other channels. An app turn reads and writes none of the viewer's memory and never shows in
WebChat. Each app has a daily cap on asks for the whole household. The deployment sets a default, and
a household owner can change it for the household and for each app. An owner who shares an app as an
app space sees and approves which servers its asks may use, as they approve its reads today.
`./check` makes real asks, so the agent proves its instructions and schema before publishing.

## User Stories

1. As a person, I want Jonas to build me an app that answers questions about my data, so that I get a Sugary-like tool without waiting for a developer.
2. As a person using a built app, I want to type or photograph something and get a structured answer the app can show, so that the app does the thinking for me.
3. As a person, I want to answer a follow-up question the app's model asked, in the same conversation, so that it does not start over.
4. As a person, I want to attach a photo from my camera to an ask, so that an app can estimate a meal or read a label.
5. As a person, I want an app's asks to keep out of my memory, so that app text, possibly copied from the web, never becomes something the agent "knows" about me.
6. As a person, I want an app's asks to stay out of my WebChat topics, so that a frequently used app doesn't flood my conversations.
7. As a person, I want an app's asks to act as me, so that an app reads my data and not another member's.
8. As a person, I want an ask that would need my approval to be refused at once, so that an app never leaves an approval prompt hanging in my WebChat or Telegram.
9. As a person, I want to see a clear error in the app when the household's daily cap is reached, so that I know why it stopped answering.
10. As a person, I want an app whose page I closed mid-ask to stop its turn, so that nobody pays for an answer nobody will read.
11. As a person, I want a timed-out or failed ask to reach the app as an error it can show, so that the app is never left waiting forever.
12. As the building agent, I want to write the app's instructions as a file in its repository, so that I can version, edit, check and publish them like the rest of the app.
13. As the building agent, I want the app manifest to name the servers its asks may use, so that the app's turns get the tools they need and nothing more.
14. As the building agent, I want to name one of my patchable models in the manifest, so that a quick classification can run on a cheaper model or a photo on a vision model.
15. As the building agent, I want to send a strict JSON schema on each ask, so that the app gets an answer it can parse, as Sugary does.
16. As the building agent, I want `./check` to make real asks against the preview's instructions, so that I can see the schema and instructions work before publishing.
17. As the building agent, I want a failed ask in somebody's browser to land in the app's runtime errors, so that I can fix it.
18. As the building agent, I want the `apps` skill to teach me asks (instructions, schemas, files, follow-ups, errors, the cap), so that I build them right the first time.
19. As the building agent, I want an ask from the preview to use the working tree's instructions and manifest, so that I can iterate without publishing.
20. As a viewer of a published app, I want asks to use the published commit's instructions and manifest, so that an unpublished edit changes nothing I see.
21. As a household owner, I want to see which servers a shared app's asks may use when I approve sharing it, so that a shared app cannot quietly operate the household's devices.
22. As a household owner, I want servers I have not approved left out of a shared app's asks, so that a publish that widens an app waits on me.
23. As a household owner, I want to set a default daily cap on asks for the household's apps, so that a runaway app can't spend the household's credit.
24. As a household owner, I want to change the cap for one app, so that an app people use a lot isn't starved and a test app stays cheap.
25. As a household owner, I want app asks to show in usage as the app channel, under the person who asked, so that I can see what apps cost.
26. As the operator, I want asks to need the household's `apps` feature, so that a household without built apps never starts an app turn.
27. As the operator, I want an app turn's tools limited to tools that never ask, so that nothing an app's code wrote can be used to talk a person into approving a call.
28. As the operator, I want the agent to believe only the apps server's own channel connection about which app a turn is for and what instructions and servers it carries, so that no other channel can start an app turn.
29. As the operator, I want an app turn admitted only on the viewer's `apps` assertion, or a check's own call token, so that an app turn never acts for anyone the accounts service did not vouch for.
30. As the operator, I want a building agent the household no longer has to refuse every ask by name, so that an app never answers with another agent.
31. As the operator, I want a suspended household or a disabled member to start no app turn, so that asks follow the same standing rules as any turn.
32. As a person, I want a follow-up to work only for the app and person that started the conversation, so that another app or member can't continue it.
33. As a person, I want images I attach to be fetched only for the turn they belong to, so that another household or app never reaches them.
34. As the operator, I want asks bounded per request (text length, files, sizes), so that one ask can't overload the agent or the store.

## Implementation Decisions

**The apps server becomes a channel.** It is dialled as a channel endpoint (`apps`) at the same MCP
address it already serves as a shared server, as Sugary is both. An ask is a channel message whose
turn the apps server waits on, as Sugary's estimate is. It names the building agent recorded on the
app as the message's agent, carries the viewer's `apps` assertion, is sent under a turn key of its
own, and is cancelled when the page gives up or the turn times out. Its `request_approval` is never
reached (below). The channel id equals the `apps` assertion channel, so the agent's turn admission
holds the assertion to it.

**The origin's ask route.** `POST /_ask` on an app's origin is settled exactly as a read is: from a
page of that origin alone, the viewer settled before the app is looked up, and the one 404 for anyone
who may not open the app. It takes the text, an optional schema (name and JSON schema, strict, as
ADR 0044 requires), an optional conversation id, and image files. It waits for the whole reply and
answers `no-store`:

- 200 with the conversation id and either the parsed JSON (schema asked) or the text.
- A refusal in the tools' envelope shape, with codes the client library maps to `apps.AskError`.
  The codes cover: the cap reached, permission denied (no instructions, a building agent the
  household no longer has, standing), an unknown or expired conversation, a reply that did not parse
  against the schema (the raw text is kept for the author only), timeout, and the agent unavailable.
  An unapproved server is no refusal: it is left out of the ask (below). On the preview alone, which
  only the author opens, a failed turn also carries `failure`, the agent's own words for it (often the
  provider's reason: a strict schema or a model it would not take), so the author can fix it; the
  published origin's viewers never read it.

Limits on text length, file count, file size and image types are apps-server settings.

**The app manifest gains three fields.**
- `instructions`: the path of a markdown file in the repository. An app without it cannot ask.
- `asks`: the server names whose tools its turns may use. Empty means no tools.
- `model`: optional, one of the building agent's patchable models.

`AppManifest.Parse` judges their shape. `./publish` refuses an `instructions` path that is not a
file of the commit within a size bound. Asks from a published origin use the served commit's
manifest and instructions. Asks from the preview use the working tree's. A shared app's asks are
narrowed to its approval, like its reads.

**The approval covers asks.** `AppReach` gains `asks`, so `Within` and `Beyond` cover it. An app
space's approved reach records which servers its asks may use. The owner's sharing page shows them,
and the published-apps listing carries them. A publish that asks beyond the approval says so, as
reads do. On a shared app, the turn is offered only the approved servers. An unapproved one is left
out rather than the ask refused whole, as the origin is granted what its manifest asks `Within` the
approval. Instructions are part of the commit and are not approved separately: only servers are.

**What the message carries: the app turn.** Beside the existing `ResponseFormat`, `ConfigPatch` and
`Attachments`, the message carries an app-turn descriptor: the app's id, its instructions text, and
the servers its asks may use (already narrowed). The agent honours this field only on a message
from the `apps` channel endpoint. On any other channel it is ignored and logged. The manifest's
`model` travels as the existing config patch, so an unknown model falls back to the agent's own, as
any patch does. The schema travels as the existing response format, strict.

**What the agent does with an app turn.**
- **Instructions.** The system prompt is the app's instructions in place of the agent's persona:
  identity, custom instructions, subagents, channel and voice sections, and memory are left out. The
  sections every agent carries whatever its persona are kept: the core directive, date and zone,
  language, the offered servers' mount and prompt sections, and their skills. Each app's prompt is
  stable across its asks, so it caches as a prefix.
- **Tools.** The turn's session is built from the building agent's servers intersected with the
  app turn's servers, so tools, mounts, prompts and skills come from those servers alone. The
  agent's whitelist still applies. Outposts are never offered to an ask, whatever the agent's
  `usesOutposts` says: a machine is somebody's own computer, reached for the person at it.
- **Approvals.** Any call whose approval would be asked, by the whitelist, the exec screen, Jev or
  as-person browsing, is refused at once with the usual rejection result. It is not sent to the
  anchor and not offered on any channel.
- **Memory.** Recall and extraction are both skipped for an app turn, read off the turn's own
  message the way the Lemonade boundary reads the patch. The skip fails closed.
- **Admission and standing.** Admission is the viewer's `apps` assertion, verified for the `apps`
  channel. For a check, it is instead the agent's own call token for the apps server naming a person,
  as `CheckCallVerifier` believes for reads, and never both. Then the household's standing, the
  `apps` feature, and the building agent's existence apply. A household without the `apps` feature
  is refused like Sugary's feature refusal.
- **History and usage.** History is persisted under the channel's conversation as any turn's, with
  no topic made. Retention applies as to any conversation. Token usage is counted under the household
  and the viewer on channel `apps`, with the app named.

**Conversations.** The apps server mints the conversation id on the first ask and binds it to the
app and the viewer. A follow-up from another app or person, or after the binding expired (a
follow-up window setting, in memory, as Sugary's open estimates are), is refused before any turn is
sent. A follow-up's earlier images rehydrate from history, as Sugary's follow-ups do.

**Files.** Images are stored in an upload store on the apps server's volume and named by reference on
the message. The agent fetches them through `fetch_attachment` on the apps channel, scoped to the
household, as for any channel with a store (ADR 0020/0021). A sweeper removes them after the
attachment retention. A reference is served only for the household and conversation it was stored
for.

**The cap.** Asks are counted per (household, app) over a rolling 24 hours. They are counted when a
turn is sent; a refusal before sending counts nothing. Counts are kept durably on the apps server's
volume, so a restart does not reset them. The limit is the app's override, else the household's
default, else the deployment's default (an apps-server setting). The household's values are a new
part of the household configuration, saved by owners on their apps admin page. The apps server
reads them through a view, as it reads `spaces`, and a value it cannot read, for any reason (an outage,
a timeout, an answer that does not parse), refuses asks (`transient_dependency`), except that the
accounts service refusing the viewer's proof refuses them `permission_denied`. It never lifts the cap.
Check asks count toward the same cap.

**Checks.** An ask during `./check` acts for the check's person, the author, on the check's own app
session and call token, against the preview's manifest and instructions. A check's failed ask is
listed in its summary beside refused reads.

**Runtime errors.** The client library reports a failed ask to `/_errors` as it does a failed read.
A member's report is reduced to the ask's error code alone.

**The `apps` skill** teaches asks: writing instructions, declaring `asks` and `model`, strict
schemas, files, follow-ups, the error codes, the cap, and checking an app that asks.

**Wiring.** The `apps` channel endpoint in the agent's settings, with its default-agent fallback
unused because a message always names its agent. The upload store's volume and settings go in the
apps server's settings. Every wiring rule that has a conformance test today (credentials, routes off
the proxy) gains its counterpart.

## Testing Decisions

Good tests here drive the feature from outside: an app origin's HTTP route and what crosses the MCP
wire, or what the agent sends the model. They never test internals such as how the session is
narrowed. Four seams:

1. **The app origin over the wire.** Sign in to an origin, `POST /_ask`, with the agent played by a
   real channel connection over MCP. This follows `SugaryEstimateApiTests` and `AppsReadsTests`, on
   the `AppsServer` fixture. It covers:
   - the message: agent, assertion, the app turn, schema, model patch and attachments;
   - the answer: parsed JSON, text, a reply that doesn't parse, timeout, cancellation when the page
     aborts, the agent unavailable;
   - follow-ups bound to the app and viewer, and expired ones;
   - published versus preview manifest and instructions, approval narrowing on a shared app;
   - the cap: override, household default, deployment default, unreadable config, rolling window,
     survival across a restart;
   - check asks, and a member's reduced error report.
2. **The agent turn.** An `apps` channel message into the real agent with FakeOpenRouter recording
   what the model is sent. This follows `AppReadsApiTests` and the channel-connection tests. It
   covers:
   - the instructions composed from the app's, and only the declared servers' tools and mounts;
   - no recall and no extraction;
   - an asked call refused with no `request_approval` and no `offer_approval`;
   - the app turn ignored from another channel;
   - admission by `apps` assertion or check token, never both, and every unbelieved shape;
   - a removed building agent, the `apps` feature off, a suspended household or disabled member;
   - usage counted on channel `apps` with the app.
3. **The browser.** One Chromium case in `AppsServingInABrowserTests`: `apps.ask` from a served page,
   with a schema and an image, a follow-up, and an `AskError`.
4. **Owner admin.** The existing owner admin and `AppSharedFacts` tests gain the approved asks and the
   cap settings: save, merge per item, and bounds.

Plus: `AppManifest` parse cases for the three fields, publish refusals for `instructions`, and
`AppsSkillTests` for every new name the skill teaches. Conformance tests cover the new channel
endpoint and the upload volume.

## Out of Scope

- Streaming replies. `apps.ask` resolves with the whole reply.
- App writes other than through tools that never ask. No app data store, no `apps.write`.
- Tools that ask for approval, and any approval UI inside an app.
- Showing app conversations in WebChat (no review copy).
- Recall or extraction of memory for app turns.
- Non-image files (audio, video, documents).
- Asks from registered (custom) agents' apps, and asks across households.
- Per-viewer caps, and money-denominated caps.
- Agent-initiated pushes into an app. The app still polls.

## Further Notes

- This reverses part of the app space spec's out-of-scope line ("built apps that … call a model").
  That spec's reads, sign-in, sharing and checks are reused unchanged.
- App code is written by the agent and can carry fetched web text into an ask. Limiting tools to
  those that never ask, refusing approvals without offering them, and keeping memory out are what
  bound that. Write-capable tools that never ask (if any) are reachable on purpose: the user chose to
  allow them rather than keep a separate read-only filter.
- The upload store and the turn wait mirror Sugary's (`UploadStore`, `AgentTurns`, `OpenEstimates`).
  Extracting the shared shape into `Mcp.Hosting` is a likely prefactor.
