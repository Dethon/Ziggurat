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

- [ ] `AppManifest` takes `instructions`. Its shape is judged on every way in, and `./publish` refuses a path that is no file of the commit, or one over the size bound.
- [ ] The published origin asks with the served commit's instructions and the preview with the working tree's (origin seam).
- [ ] An app without instructions is refused `permission_denied`; anyone who may not open the app gets the one 404 (origin seam).
- [ ] The message names the building agent, carries the viewer's assertion and the app turn, and is sent under its own turn key (origin seam).
- [ ] The page giving up cancels the turn. The timeout cancels it and answers the timeout code. The agent unreachable answers unavailable (origin seam).
- [x] The model is sent the app's instructions with no persona, custom instructions, subagents, channel, voice or memory sections (agent seam, FakeOpenRouter).
- [x] Neither recall nor an extraction enqueue happens for an app turn (agent seam).
- [x] An app turn on any channel but `apps` is ignored and logged (agent seam).
- [x] Admission refuses: no assertion, a non-`apps` assertion, the guest, a suspended household, a disabled member, the `apps` feature off, and a building agent the household no longer has (named). Each comes back to the app as `AskError` (agent seam).
- [x] No topic is minted, and usage is counted on channel `apps` under the viewer with the app id (agent seam).
- [ ] The client library exposes `apps.ask` and `apps.AskError`, from the classic script and the module.
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
