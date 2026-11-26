# 02: An app asks and gets text back

Spec: `.scratch/app-asks/spec.md` ("Solution"; "The apps server becomes a channel"; "The origin's ask route"; "What the message carries"; "What the agent does with an app turn").

**What to build:** The tracer bullet. An app whose manifest names an `instructions` file calls
`await apps.ask("…")` and gets the agent's text back.
- **The apps server.** It is dialled as the `apps` channel endpoint. `POST /_ask` on the origin is
  settled as a read is, then sent as a channel message. The message names the building agent, carries
  the viewer's `apps` assertion and the app turn (the app id, the instructions text, and no servers
  yet). The apps server then waits on the reply.
- **The agent.**
  - It honours the app turn only from the `apps` endpoint and admits the turn on the `apps`
    assertion.
  - The system prompt is the app's instructions plus the sections every agent carries, not the
    persona. The turn has no tools.
  - It neither recalls nor extracts memory, makes no WebChat topic, and counts usage on channel
    `apps` with the app named.

**Blocked by:** 01

**Status:** ready-for-agent

- [x] `AppManifest` takes `instructions`. Its shape is judged on every way in, and `./publish` refuses a path that is no file of the commit, or one over the size bound.
- [x] The published origin asks with the served commit's instructions and the preview with the working tree's (origin seam).
- [x] An app without instructions is refused `permission_denied`; anyone who may not open the app gets the one 404 (origin seam).
- [x] The message names the building agent, carries the viewer's assertion and the app turn, and is sent under its own turn key (origin seam).
- [x] The page giving up cancels the turn. The timeout cancels it and answers the timeout code. The agent unreachable answers unavailable (origin seam).
- [x] The model is sent the app's instructions with no persona, custom instructions, subagents, channel, voice or memory sections (agent seam, FakeOpenRouter).
- [x] Neither recall nor an extraction enqueue happens for an app turn (agent seam).
- [x] An app turn on any channel but `apps` is ignored and logged (agent seam).
- [x] Admission refuses: no assertion, a non-`apps` assertion, the guest, a suspended household, a disabled member, the `apps` feature off, and a building agent the household no longer has (named). Each comes back to the app as `AskError` (agent seam).
- [x] No topic is minted, and usage is counted on channel `apps` under the viewer with the app id (agent seam).
- [x] The client library exposes `apps.ask` and `apps.AskError`, from the classic script and the module.
- [x] Wiring: the `apps` channel endpoint in the agent's settings; conformance tests updated.

## Comments

**Agent side done (2026-11-26).** What the apps server must send, and what it gets back:

- The `apps` channel endpoint is dialled at `http://mcp-apps:8080/mcp` (`appsettings.Local.json`:
  `http://localhost:6018/mcp`), with no default agent. A message on it that names no agent is never routed
  to the household's default, and one with no `appTurn` is refused.
- Send each ask as a `ChannelMessageNotification` with `agentId` (the building agent from `app.meta.json`),
  `assertion` (the session's `apps` assertion, renewed first if due), `principal` (`apps`, the viewer's user
  id, their account; a label only), `sender` (a label), a `turnKey` of the server's own, the conversation id
  it minted, and `appTurn`: `{"appId": "...", "instructions": "...", "servers": []}` (`Domain/DTOs/Channel/AppTurn.cs`).
  `reviewCopy` and `replyTo` are dropped by the agent.
- Every refusal is an `Error` reply then a `StreamComplete`, both under the ask's `turnKey`. Its text is a
  `RefusalAnswers` sentence, and `RefusalAnswers.CodeOf(text)` gives its code: `not-verified` (no
  assertion, another channel's, the guest's, nobody's, or no `appTurn`), `suspended`, `disabled`,
  `unchecked`, `apps-off`, `agent-gone` (the text names the agent), `without-key`. `SetupFailed` has no code.
- **A cancel must name the agent**: messages group by (conversation, agent) and an apps-channel message is
  never routed to a default, so a `ChannelCancelNotification` without `agentId` never reaches the ask's
  turn. `AgentTurns.CancelAsync` (`Mcp.Hosting`) sends none today; the apps side has to carry it.
- `key-refused` and `out-of-credit` arrive as in-turn errors (an `Error` reply in the turn), not admission
  refusals. A message on the apps channel with no `appTurn` being refused is the agent's own fail-closed
  addition, not the spec's.
- An ask dials none of the building agent's servers (no warmup, no session) while `servers` offers
  nothing; ticket 05 builds the narrowed session.
- Until the apps server serves the channel tools, the agent's `apps` connection fails and retries. The E2E
  (`WebChatStack`) and eval stacks list their own `channelEndpoints` and need `apps` added when they ask.
- The `app_instructions` section is budgeted at 4,000 tokens: keep the publish bound on the instructions
  file within it.
- Tests: `ChatMonitorAppTurnTests` (trust, admission, refusals, no topic), `AppTurnAgentTests` (FakeOpenRouter:
  instructions, empty tools, no skills, no recall or extraction, usage), `MemoryRecallHookTests`,
  `PromptComposerTests`, `McpChannelConnectionParsingTests`, `AgentAppSettingsTests`.

**Apps server side done (2026-11-26).** What an app sends and gets back, and what tickets 03-10 build on:

- **The wire** (`Domain/Apps/AppAsks`): `POST /_ask` on the app's origin, body `{"text": "..."}` parsed
  strictly (an unknown field such as `conversation` or `schema` is refused whole until its ticket adds it;
  `Asks:MaxTextLength`, 8,000). Settled exactly as a read (`AppPages.AskAsync`: this origin's page alone, the
  viewer before the app, the one 404 before the body is read), then `AgentAsks`. Answers, all `no-store`:
  200 `{"conversation", "text"}`; 400 `invalid_argument` (body); 422 `permission_denied` (no instructions,
  the preview's instructions missing/over the bound/empty, a check's session, and every agent refusal but
  `unchecked`, its `RefusalAnswers` sentence as the message so `agent-gone` names the agent); 422
  `transient_dependency` (`unchecked`, a failed turn whose words are the agent's exception, kept to the log,
  and a turn ending with no text); 503 `transient_dependency` (nobody polling); 504 `timeout`
  (`Asks:Timeout`, 2 min). The page aborting the request cancels the turn.
- **The message**: `AgentId` = `app.meta.json`'s building agent, `Assertion` = the session's `apps`
  assertion (renewed by settling if due), `Principal` = (`apps`, viewer user id, viewer `Account`), `Sender`
  = the viewer's user id, `AppTurn` = { app id, the origin's own manifest's instructions text (published: the
  served commit's; preview: the tree's), `Servers: []` }, sent by `AgentTurns` under its own turn key.
- **A cancel names the agent**: `AgentTurns` now carries the message's `AgentId` on its cancel (Sugary's
  messages name none, so its cancels are as before; `AgentTurnsTests`).
- **Conversations (for 04)**: `McpServerApps/Asks/AskConversations` wraps
  `OpenConversations<(AppId, Account viewer), string agentId>` with `Asks:FollowUpWindow` (24 h, added now
  because the shared piece needs a window). Each ask opens a fresh one today; 04 adds `TryContinue` and the
  request's `conversation` field. The carried agent id is what a follow-up must name.
- **The channel**: the apps server is dual-role, gate-on-live. Sugary's `send_reply`/`request_approval`
  moved into `Mcp.Hosting` as `AgentTurnTools`, registered with the table by `AddAgentTurns()`, which both
  servers call. No `register_agents` (nothing here reads the catalogue; the agent tolerates its absence).
  `WebChatStack` now lists the `apps` channel endpoint; the eval stack lists none.
- **Instructions**: `AppManifest.Instructions`, a repository-relative `.md` path (no leading `/`, `\`,
  empty, `.`/`..`, `.git` or `node_modules` segment). Deviation: the bound is a constant,
  `AppAsks.MaxInstructionsBytes` = 16,000 bytes, not a setting, because a deployment raising it would
  overrun the `app_instructions` budget (16,000 UTF-8 bytes are at most 16,000 characters, 4,000 tokens at
  `PromptTokens.Estimate`; `AppAsksTests` pins it to the declared budget). `./publish` refuses a file that is
  missing, a link (any segment), empty, not UTF-8 or over it; each ask reads it again the same way.
- **Client library**: `apps.ask(text, { signal, ...options })` resolves `{ conversation, text }` and rejects
  with `apps.AskError` (`errorCode`, `message`, `retryable`, `hint`, `envelope`), from the classic script and
  the module (`ask`, `AskError` exported). Other options are sent as body fields, so the server refuses an
  option it does not take yet. `/_ask` is in the worker's passed-by list. A failed ask is not yet reported to
  `/_errors` as such (ticket 10); an uncaught `AskError` is reported as any rejection is.
- **The `apps` skill** gained one short "Asking the assistant" paragraph (`apps.ask`, `instructions`,
  `apps.AskError`; body budget 1,840), because `AppsSkillTests` holds every library export to the skill.
  Ticket 11 still owns teaching asks properly.
- **Tests**: `AppsAsksTests` (origin seam, agent played by a real channel connection), `AppAsksTests`,
  `AppManifestTests`, `AppsPublishingTests`, `AgentTurnsTests`, `AppsServingInABrowserTests` (one Chromium
  case, `apps.ask` from the module and the script, an `AskError`), the server table row (dual-role,
  gate-on-live), `AppsShippedSettingsTests`.


**Usage read back (2026-11-26, story 25, in part).** The counted `AppId` is now read (`Channel` is not): the owner's usage
(`/admin/usage`) has a "Per app" table, keyed by `TokenUsageEvent.AppId` in `MetricsQueryService.GetHouseholdUsageAsync`
(`HouseholdUsage.Apps` → `AdminUsage.Apps`), named by the household's published listing now in
`HouseholdUsageAdmin` (the apps server asked only when an app was; an unlisted app keeps its id). An ask's cost
stays in the asker's and the agent's lines too. Not built: a per-channel breakdown anywhere — on the owner's
page its rows would be raw channel ids (`signalr`, `telegram`, `scheduling`) needing owner-facing labels, and on the
operator's dashboard a `TokenDimension.Channel`, which the truncation grouping would also have to answer
(`ContextTruncationEvent` carries no channel). Tests:
`HouseholdUsageAdminTests` (two cases), `HouseholdUsageE2ETests`.
