---
paths:
  - "McpServerTimers/**"
  - "Domain/Tools/Timers/**"
  - "Domain/Prompts/TimerPrompt.cs"
  - "Domain/Prompts/CountdownTimersSkill.cs"
  - "Domain/Contracts/IInsistentAnnouncer.cs"
  - "Domain/Contracts/IAlertDismisser.cs"
  - "Domain/Contracts/ISatelliteCatalog.cs"
  - "Domain/Contracts/IHouseholdVoices.cs"
  - "Domain/Contracts/IChannelAlerts.cs"
  - "Domain/Contracts/IAlertChannel.cs"
  - "Domain/Contracts/ITimerStore.cs"
  - "Domain/DTOs/Voice/ArmedTimer.cs"
  - "Domain/DTOs/Channel/ChannelAlert.cs"
  - "Domain/Monitor/AlertRelay.cs"
  - "Infrastructure/Clients/Voice/**"
  - "Infrastructure/Timers/**"
  - "McpChannelVoice/Services/VoiceHubAuth.cs"
  - "McpChannelSignalR/Services/AlertService.cs"
  - "McpChannelTelegram/McpTools/*Alert*"
  - "WebChat.Client/State/Alert/**"
  - "WebChat.Client/Components/AlertModal.razor"
---

# Timers Architecture

Countdown timers live in **`McpServerTimers`** (container `mcp-timers`, port 6016), a **shared server** (every household's) exposing `filesystem://timers` (mount `/timers`). Agents mount it via `mcpServerEndpoints`. It is **dual-role** (`.claude/rules/channels.md`): a timer with no satellite to ring on rings on its owner's channels, which only the agent holds, so the server is also a channel the agent dials (`channelEndpoints` id `timers`) — it raises alerts and never a turn. `TimerFileSystem` (`Domain/Tools/Timers/Vfs/`), the non-durable `InMemoryTimerStore`, `TimerFireService` (polls for due timers) and `ChannelAlerts` all run here.

## A timer is its household's

- **Recorded from the verified call.** The mount answers each call as the caller sees it (`TimerFileSystem.For(FileSystemCaller)`): the household is the call token's `tid` — a person's or the household's guest's — and the owner is the person's `Account` (none for the guest), with the agent the turn ran on. `ArmedTimer` carries `TenantId`, `Owner` and `AgentId`. A call acting for nobody has no household and is refused every operation (`PermissionDenied`), never shown somebody's timers; the server's own instance is that nobody, so a test or a seed goes through `For` (`EvalStack.ArmAsync` as the eval account).
- **Listed, read, searched, cancelled and dismissed by its household alone.** `ITimerStore` keys by `(TenantId, Id)` — ids are the model's to choose, so two households' `pasta` are two timers — and every call but the fire loop's claim names the household.
- **Where it rings is the target's.** A `target` (`{satelliteId | satelliteIds | room | all}`) rings on the household's satellites through its own voice server; **no target rings on the owner's channels** — their chat client (and its push) and their Telegram — which is how a household with no satellites has timers at all. The guest has no channels, so its timer with no target is refused at create.
- **The household's satellites are read per call**, from the read-only `/timers/satellites.json` (`TimerPath.SatellitesFileName`) through the call's household: `{satellites: [{id, room}]}`, or none with a note when its voice server is not connected. **Not from the prompt:** `timers_prompt` is fetched for nobody while a session is built and cached once per shared server for every household, so a roster there would show every household the one it was fetched from. `TimersSystemPrompt` serves `TimerPrompt.Build([])`, the static text, for everyone. The `countdown-timers` skill says to read the file on a non-voice turn and to leave the target out without asking when it lists none.

## Ringing on a satellite: the household's voice server, through the relay

The three things a satellite timer needs that only exist in the voice hub go over HTTP, so the hub stays the single source of truth for ringing and satellite resolution:
- **Fire** — `TimerFireService` POSTs to `/api/voice/announce` (`HttpInsistentAnnouncer`); ringing runs on the hub's live Wyoming sessions.
- **Dismiss** — `exec ./dismiss` (an action file: info and glob report it executable, read and write refuse it) POSTs to `/api/voice/dismiss` (`HttpAlertDismisser`), cancelling the hub's live `ActiveAlertRegistry` CTSs. Wake/button dismissal stays hub-local.
- **Validate target** — `CreateAsync` resolves via `GET /api/voice/satellites` (roster, fetched fresh per call — it only changes on hub restart) + `POST /api/voice/satellites/resolve` (`HttpSatelliteCatalog`). Resolution is **never** done in the timers process: the hub's `SatelliteRegistry` dual-keys rooms on `Room` and `DisplayLocation`, so forwarding keeps create-time validation byte-identical to fire-time routing.

**Bound per household.** `IHouseholdVoices.For(tenantId)` (`HttpHouseholdVoices`) hands the three adapters bound to one household; each request names it in `X-Ziggurat-Household` (`RelayedCallProtocol.HouseholdHeader`, beside the announce token, `VoiceHubHttp.Address`). The mount uses its caller's household; the fire loop the timer's record. The Domain contracts `IInsistentAnnouncer`, `IAlertDismisser` and `ISatelliteCatalog` keep no household (voice implements the first in process, and Home Assistant's server dials its own household's hub directly), which is why the binding is a factory rather than a parameter.

All three endpoints share `VoiceHubAuth` (token via `X-Announce-Token`/`Announce__Token`; `BindToLoopbackOnly` must stay false so the separate container can reach them); `HttpClient.BaseAddress` comes from `VoiceHub__BaseUrl`. **The voice server is a household's** and has no address on the deployment's network: the deployment's `VOICEHUB__BASEURL` is the relay's endpoint on the agent, `http://agent:8080/internal/relay/voice/`, which forwards each call down **the named household's** voice server's tunnel (`.claude/rules/tunnel.md`, "A shared server's calls to a household's server"). Under that path the client presents this server's name (`Mcp:Audience`) and its own `/mcp` credential, which the agent holds to dial it (`RelayedCallCredential`), and holds no announce token: the relay presents the voice server its tunnel's credential, which `VoiceHubAuth` takes while tunnelled. A request naming no household is refused (403), never sent to the home's. An offline voice server is the relay's 502, which `VoiceHubHttp` reads as the hub unreachable — a create with a target fails `TransientDependency` and arms nothing (its hint offers leaving the target out, for a person); a dismiss with nothing ringing on channels answers the same, saying nothing rings on the household's chat clients or Telegram. `IInsistentAnnouncer`, `IAlertDismisser` and `ISatelliteCatalog` (`Domain/Contracts`) are async precisely so the same engine code runs unchanged in-process or over HTTP.

## Ringing on the owner's channels: through the agent

A timer with no target rings as an **insistent alert** on its owner's channels. Only the agent holds channel connections and credentials, so it flows as a fire does — through the agent — and ends as an approval ends: the first dismissal settles it everywhere.

1. **Raised.** At its due second `TimerFireService` hands it to `ChannelAlerts.RingAsync` (`IChannelAlerts`), which mints a whole-GUID alert id, holds it as ringing under the household, and emits a `ChannelAlertNotification` (alert id, owner `Account`, text — the timer's or `<id> timer` —, kind, agent) as the inbox item `ChannelInboxItemKind.Alert` (`ChannelNotificationEmitter.EmitAlertAsync`). Gate-on-live, as scheduling's: the timer was claimed once, so an alert no agent was polling to take is logged not rung, the same accepted trade as a hub unreachable at the due second.
2. **Offered.** The agent's connection to the timers server reads it apart from turns (`McpChannelConnection.Alerts`, an `AlertSignal.Ring`), and the agent's one **`AlertRelay`** (`Domain/Monitor`, hosted by `AlertRinging`, registered by `AddAlertRinging`) offers it with `offer_alert` on every other connected channel that advertises the tool — the chat client and Telegram — signed for nobody, naming the owner as an approval offer names its asker.
3. **Settled.** The first channel answering `dismissed` ends it: every other channel, the timers server included, gets `settle_alert` (`dismissed: true`) and its offer is called off. `./dismiss` on the timers server stops the household's ringing alerts there (`ChannelAlerts.StopAllAsync`, reported in the exec's stdout like a satellite's), emitting `ChannelInboxItemKind.AlertStopped`, and the relay settles every channel still showing it. An alert no channel could show is settled at its origin at once (rung nowhere); one nobody dismissed rings out after `AlertRelay.RingWindow` (two minutes, a satellite's twelve rounds ten seconds apart) and is settled everywhere with `dismissed: false`.

Where it shows: the chat client (`AlertService`: `OnAlertOffered` to the owner's `account:{userId}` group whatever space, a Web Push to their devices always, `DismissAlert` from any of their clients, `GetOfferedAlerts` to catch a client up; the client's `AlertModal` sounds a Web Audio tone and buzzes until it ends) and Telegram (the owner's private chat by the setting agent's bot, else the first other bot they opened a chat with, one Dismiss button whose tap counts for the owner alone, every copy edited when it ends). A dismissal by anyone but the owner answers nothing. The owner the alert names is the timers server's word, which it took from the verified call that armed the timer: the agent believes a deployment server that presents its credential for who an alert is for, as it does an approval's asker — an alert shows the person text and acts as nobody.

The `timers_prompt` (`Domain/Prompts/TimerPrompt.cs`) is the standing stub — which mechanism a moment is, that whatever rings now is silenced through `/timers`. The doing rules live in the `countdown-timers` skill (`Domain/Prompts/CountdownTimersSkill.cs`, shipped through `AddSkills`, loaded by the model on a timer turn): the file's shape, `satellites.json`, and the target branch on channel — on a voice turn default to the speaking room; elsewhere read the household's satellites, leave the target out when there are none, else ask which room. See `docs/adr/0039`. Timers are in memory, so nothing about them is migrated.
