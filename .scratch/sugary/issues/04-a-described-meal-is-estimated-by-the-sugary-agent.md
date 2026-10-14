# 04 — A described meal is estimated by the sugary agent

**What to build:** The Sugary server becomes a channel server. From the app, she types a description of a meal and gets an estimate produced by the new `sugary` agent: the channel opens a conversation for the meal, sends a turn asking for the meal estimate's response format, collects the whole reply, parses it against the schema, computes the totals and the range floor (0.6× to 1.6× of the best weight on every item not marked high confidence) and answers the waiting app request. See the spec's "The channel role" and "The agent".

**Blocked by:** 02, 03

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] A `sugary` agent definition with the Sugary server as its only endpoint, memory and subagents off, Gemini 3.8 Flash as its model and Haiku 5.5 among its patchable models.
- [x] `agentDefaults` routes the Sugary channel's messages to `sugary`.
- [x] The estimating prompt is a section served by the Sugary server, declared in the prompt manifest with its budget, and carries the never-suggest-doses rule; the staleness and budget tests pass.
- [x] The server ships a skill explaining her foods and the tables.
- [x] The channel's delivery policy is `GateOnLive`; its channel receive contract row passes.
- [x] Totals always equal the sum of the items, and the range floor is applied in code; tests pin both at the server's boundary.
- [x] A reply that does not match the schema, or a turn that ends with no reply, answers the app with an error and is never shown half-read.
- [x] The estimate is written in the app's language, English or Spanish.
- [x] Tests play the agent over MCP against the real server, like the channel receive and send-reply contract tests.
- [x] The cost of each estimate appears on the dashboard with every other turn.

## Comments

**Built (2026-10-14).** `McpServerSugary` is dual-role (`AddToolServer` then `AddChannelServer(GateOnLive)`;
its row in `McpServerRegistrations` is `DualRole` with the policy, so the channel receive and
send-reply contract rows and the dual-role tool row run for it). `POST /api/estimates` is a turn of
the new `sugary` agent:

1. `EstimateEndpoints` reads the app's form unchanged (`description`, `clarifications`, `counted`,
   `language`; `photos` refused, see below) and builds the turn's text with `MealTurn.Text`, the
   standalone `UserText` word for word, language line included.
2. `SugaryChannel.AskAsync` opens a `PendingTurn` under a fresh `TurnKey` (`AgentTurns`), emits a
   `ChannelMessageNotification` on a new conversation id with `ResponseFormat =
   MealEstimateSchema.Format` and that turn key, and waits for the reply.
3. The agent's `send_reply` (`McpTools/SendReplyTool`) hands every chunk to `AgentTurns`, which
   collects text per message id under its turn key; `StreamComplete` (or `isComplete`) ends the turn
   with the **last** message's text, an `Error` chunk ends it as failed.
4. `MealEstimateReply.Read` reads that text against the schema — strictly: every field required,
   typed as asked, nothing extra, no nulls in lists — then `MealEstimate.From` (ported as it was)
   computes item carbs, the 0.6×–1.6× floor on every item not `high`, the 0–100 clamp, the trimming
   and the total as the sum of items. The app gets the standalone JSON; `GET /api/session` now says
   `estimates: true`.

The estimate logic lives in `Domain/Tools/Sugary/Estimation/` (pure, so ticket 08's eval can reuse
it); the prompt is `Domain/Prompts/SugaryPrompt.cs` (`sugary_prompt`, served by
`McpServerSugary/McpPrompts/McpSystemPrompt.cs`, budget 1,200 for 1,061 measured); the skill is
`Domain/Prompts/SugarySkill.cs` (`sugary-log`, body 403/500), shipped with `AddSkills`.

**The agent** (`Agent/appsettings.json`): `sugary`, `google/gemini-3.8-flash` (the id the standalone
benchmark used), reasoning `medium` (the standalone's), `http://mcp-sugary:8080/mcp` only, features
`filesystem.read/glob/info/search` with `domain__filesystem*` whitelisted, no memory, no subagents, no
pinned language. `agentDefaults.byChannel.sugary = sugary`; `channelEndpoints` has `sugary`, also in
`appsettings.Local.json` (port 6017); compose's `agent` depends on `mcp-sugary`. Prompt snapshot
`Tests/Snapshots/prompt.sugary.txt`, skill snapshot `skill.sugary-log.md`.

**Tests.** `Tests/Integration/McpServerTests/SugaryEstimateApiTests.cs` boots the real server and
plays the agent with the agent host's own `McpChannelConnection("sugary")`
(`SugaryServer.AgentAsync`): format, schema, text and language on the received message; the reply
sent in chunks; the app's HTTP answer. Pinned there: totals = sum of items and the range floor
(toast stated, juice widened 190/200/210 → 120/200/320); prose, a missing field or a model total →
502; a turn ending with no text → 502; an `Error` chunk (even after partial text) → 502 with its
reason; text before a tool call not read as the answer; nobody polling → 502 at once; past the wait →
504 and a cancel reaches the agent; a photo → 400; Spanish/English reach the turn. Unit:
`Tests/Unit/Domain/Sugary/MealEstimate{,Reply}Tests.cs`, `MealTurnTests.cs` (standalone tests carried
over + 13 unreadable shapes + the schema is strict-mode valid). Served prompt and skill over MCP in
`McpSugaryServerTests`; the agent's rules (only endpoint, no memory/workers, channel routed and
connected) in `AgentAppSettingsTests`.

**Cost on the dashboard: confirmed with a test.** `OpenRouterChatClient` publishes a
`TokenUsageEvent` after every streamed response whatever its options;
`OpenRouterChatClientResponseFormatTests.ATurnWithAFormat_PublishesItsTokenUsageLikeAnyTurn` pins it
for a formatted turn (sender, model, tokens). Nothing new was needed.

**Decisions to know about.**

- **The message names no agent.** `AgentId` is left null so `agentDefaults` is what routes the
  channel to `sugary`; a stamped id would make that configuration dead.
- **A turn key per estimate, minted by the channel.** Replies are matched by the `TurnKey` the agent
  echoes, not by conversation, so a cancelled turn's late chunks can never land in the next one.
- **The last message is the answer.** Message ids are one per model call, so a turn that writes text,
  calls a tool and then answers sends two messages; only the last is parsed.
- **Strict read, not the standalone's lenient one.** The standalone deserializer would have read
  `{}` as an empty meal and a missing `gramsHigh` as 0 g. Anything outside the schema is refused
  whole with "The model returned an answer that could not be read."
- **Status codes.** 400 for a photo or no description; 502 with a `detail` for nobody polling, an
  agent error, no answer or an unreadable answer (the app shows `detail`; a 503 would read in the
  app as "the AI model isn't set up"); 504 after `TurnTimeoutSeconds` (new setting, 120, in
  `appsettings.json`), with a channel cancel emitted for the conversation. A phone that drops the
  request also cancels the turn.
- **Photos are refused, not ignored** (400 "Photos cannot be estimated yet. Describe the meal in
  words instead."). The app still offers the camera now that `estimates` is on; only the
  description path works until 05.
- **`request_approval` answers `rejected`** on this channel (nobody to ask). The agent's only tools
  are the whitelisted read-only mount tools and `load_skill`, which is always approved, so nothing
  should reach it.
- **The agent can read `/sugary`.** The skill explains tables only the mount serves, so the agent
  gets the read-only file tools. An estimate turn may therefore call a tool before answering
  (ADR-0044 allows it). The estimating prompt does not mention the mount: its words are the
  standalone's, unchanged apart from a `## Estimating carbs` heading.
- **The dose rule beats the core directive.** `sugary_prompt` governs `refusals` and declares it
  beats `core_directive` ("do not refuse"), which the assembly checks. Its claim
  `sugary.never-suggests-a-dose` and the skill's trigger claim are exempted as `Unwritten` until
  ticket 08's scenarios.
- **Opus 5.5 was added to `patchableModels`**, which is one deployment-wide list, so it now
  also appears in WebChat's model picker for every agent, not only `sugary`. Haiku 5.5 was already
  there.
- **Sender is `sugary-app`**, so the dashboard's per-sender cost shows the app's estimates apart.
- **The `sugary` agent is in the catalogue**, so WebChat lists it like any agent. A WebChat turn to
  it carries no format and gets prose.
- **Every estimate turn asks Jev for a skill preload** (global `skillPreload`, up to its 700 ms
  deadline, parallel to the turn's setup). Measure it before the switch-over if latency matters.
- **Local runs:** `appsettings.Local.json` overrides `agents[]` by index, so the `sugary` agent's
  own endpoint is not overridden locally (it still dials `mcp-sugary`); only its channel is.

**Where the later tickets plug in.**

- **05 (photos):** `EstimateEndpoints` refuses `photos` at the top; replace that with storing them in
  the channel's upload store, pass their count to `MealInput.PhotoCount` (the text already says "One
  photo…"/"N photos…"), set `Attachments` on the notification in `SugaryChannel.AskAsync`, and add a
  `fetch_attachment` tool beside `SendReplyTool`.
- **06 (answers and already-counted):** `clarifications` and `counted` already reach the turn text,
  but each request is a new conversation (`Guid` minted in `EstimateEndpoints`). 06 returns a
  conversation id with the estimate (an extra field the app ignores today) and accepts it back on the
  follow-up; `AgentTurns` already keys by turn, so several turns on one conversation work as they are.
- **07 (labels):** `UnavailableEndpoints` now maps only `/labels`. A label route calls the same
  `SugaryChannel.AskAsync` with a label schema and needs a `ConfigPatch` parameter for the label
  model (`anthropic/claude-opus-5.5`, now patchable); `SessionEndpoints` flips `Labels`.

**Not done / deferred.** No change to the PWA. No WebChat review copy (11). No eval of the agent
on the benchmark meals (08): nothing here called a real model, so whether Gemini honours the
language line and the dose rule as a turn of this agent is unmeasured.
