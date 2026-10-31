---
paths:
  - "McpServerTimers/**"
  - "Domain/Tools/Timers/**"
  - "Domain/Prompts/TimerPrompt.cs"
  - "Domain/Prompts/CountdownTimersSkill.cs"
  - "Domain/Contracts/IInsistentAnnouncer.cs"
  - "Domain/Contracts/IAlertDismisser.cs"
  - "Domain/Contracts/ISatelliteCatalog.cs"
  - "Infrastructure/Clients/Voice/**"
  - "McpChannelVoice/Services/VoiceHubAuth.cs"
---

# Timers Architecture

Countdown timers live in **`McpServerTimers`** (container `mcp-timers`, port 6016), a **pure filesystem tool server** (McpServerPrinter shape) exposing `filesystem://timers` (mount `/timers`). Agents mount it via `mcpServerEndpoints` — it is **not** a channel, because a timer just *rings*, it doesn't message the agent. `TimerFileSystem` (`Domain/Tools/Timers/Vfs/`), the non-durable `InMemoryTimerStore` and `TimerFireService` (polls for due timers) all run here.

The three things a timer needs that only exist in the voice hub go over HTTP, so the hub stays the single source of truth for ringing and satellite resolution:
- **Fire** — `TimerFireService` POSTs to `/api/voice/announce` (`HttpInsistentAnnouncer`); ringing runs on the hub's live Wyoming sessions.
- **Dismiss** — `exec ./dismiss` (an action file: info and glob report it executable, read and write refuse it) POSTs to `/api/voice/dismiss` (`HttpAlertDismisser`), cancelling the hub's live `ActiveAlertRegistry` CTSs. Wake/button dismissal stays hub-local.
- **Validate target** — `CreateAsync` resolves via `GET /api/voice/satellites` (roster, fetched fresh per call — it only changes on hub restart) + `POST /api/voice/satellites/resolve` (`HttpSatelliteCatalog`). Resolution is **never** done in the timers process: the hub's `SatelliteRegistry` dual-keys rooms on `Room` and `DisplayLocation`, so forwarding keeps create-time validation byte-identical to fire-time routing.

All three endpoints share `VoiceHubAuth` (token via `X-Announce-Token`/`Announce__Token`; `BindToLoopbackOnly` must stay false so the separate container can reach them); `HttpClient.BaseAddress` comes from `VoiceHub__BaseUrl`. **The voice server is a household's** and has no address on the deployment's network: the deployment's `VOICEHUB__BASEURL` is the relay's endpoint on the agent, `http://agent:8080/internal/relay/voice/`, which forwards each call down the voice server's tunnel (`.claude/rules/tunnel.md`, "A shared server's calls to a household's server"). Under that path the client presents this server's name (`Mcp:Audience`) and its own `/mcp` credential, which the agent holds to dial it (`RelayedCallCredential`), and holds no announce token: the relay presents the voice server its tunnel's credential, which `VoiceHubAuth` takes while tunnelled. An offline voice server is the relay's 502, which `VoiceHubHttp` reads as the hub unreachable — a create fails `TransientDependency` and arms nothing. Which household's voice: the home household's, in this phase (`HomeHousehold`); timers are not per household until 4b. `IInsistentAnnouncer`, `IAlertDismisser` and `ISatelliteCatalog` (`Domain/Contracts`) are async precisely so the same engine code runs unchanged in-process or over HTTP. The `timers_prompt` (`Domain/Prompts/TimerPrompt.cs`) is the standing stub — which mechanism a moment is, that whatever rings now is silenced through `/timers` — and embeds the live roster at prompt-fetch time (`TimersSystemPrompt` asks `ISatelliteCatalog` with a 2 s cap and fails open to roster-less static text, so an unreachable hub can never stall or fail session build). The doing rules live in the `countdown-timers` skill (`Domain/Prompts/CountdownTimersSkill.cs`, shipped through `AddSkills`, loaded by the model on a timer turn): the file's shape, and the target branch on channel — on a voice turn default to the speaking room; elsewhere offer the roster rooms and ask. See `docs/adr/0039`.
