---
paths:
  - "McpServerTimers/**"
  - "Domain/Tools/Timers/**"
  - "Domain/Prompts/TimerPrompt.cs"
  - "Domain/Prompts/CountdownTimersSkill.cs"
  - "Domain/Contracts/IInsistentAnnouncer.cs"
  - "Domain/Contracts/IAlertDismisser.cs"
  - "Domain/Contracts/ISatelliteCatalog.cs"
---

# Timers Architecture

Countdown timers live in **`McpServerTimers`** (container `mcp-timers`, port 6016), a **pure filesystem tool server** (McpServerPrinter shape) exposing `filesystem://timers` (mount `/timers`). Agents mount it via `mcpServerEndpoints` — it is **not** a channel, because a timer just *rings*, it doesn't message the agent. `TimerFileSystem` (`Domain/Tools/Timers/Vfs/`), the non-durable `InMemoryTimerStore` and `TimerFireService` (polls for due timers) all run here.

The three things a timer needs that only exist in the voice hub go over HTTP, so the hub stays the single source of truth for ringing and satellite resolution:
- **Fire** — `TimerFireService` POSTs to `/api/voice/announce` (`HttpInsistentAnnouncer`); ringing runs on the hub's live Wyoming sessions.
- **Dismiss** — `exec dismiss.sh` POSTs to `/api/voice/dismiss` (`HttpAlertDismisser`), cancelling the hub's live `ActiveAlertRegistry` CTSs. Wake/button dismissal stays hub-local.
- **Validate target** — `CreateAsync` resolves via `GET /api/voice/satellites` (roster, fetched fresh per call — it only changes on hub restart) + `POST /api/voice/satellites/resolve` (`HttpSatelliteCatalog`). Resolution is **never** done in the timers process: the hub's `SatelliteRegistry` dual-keys rooms on `Room` and `DisplayLocation`, so forwarding keeps create-time validation byte-identical to fire-time routing.

All three endpoints share `VoiceHubAuth` (token via `X-Announce-Token`/`Announce__Token`; `BindToLoopbackOnly` must stay false so the separate container can reach them); `HttpClient.BaseAddress` comes from `VoiceHub__BaseUrl`. `IInsistentAnnouncer`, `IAlertDismisser` and `ISatelliteCatalog` (`Domain/Contracts`) are async precisely so the same engine code runs unchanged in-process or over HTTP. The `timers_prompt` (`Domain/Prompts/TimerPrompt.cs`) is the standing stub — which mechanism a moment is, that whatever rings now is silenced through `/timers` — and embeds the live roster at prompt-fetch time (`TimersSystemPrompt` asks `ISatelliteCatalog` with a 2 s cap and fails open to roster-less static text, so an unreachable hub can never stall or fail session build). The doing rules live in the `countdown-timers` skill (`Domain/Prompts/CountdownTimersSkill.cs`, shipped through `AddSkills`, loaded by the model on a timer turn): the file's shape, and the target branch on channel — on a voice turn default to the speaking room; elsewhere offer the roster rooms and ask. See `docs/adr/0039`.
