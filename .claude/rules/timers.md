---
paths:
  - "McpServerTimers/**"
  - "Domain/Tools/Timers/**"
  - "Domain/Prompts/TimerPrompt.cs"
  - "Domain/Prompts/Sections/timers_prompt.md"
  - "Domain/Prompts/CountdownTimersSkill.cs"
  - "Domain/Prompts/Skills/countdown-timers.md"
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

`McpServerTimers` (container `mcp-timers`, port 6016) is a **shared server** (every household's) exposing `filesystem://timers` (mount `/timers`) through `TimerFileSystem` over the non-durable `InMemoryTimerStore`, rung by `TimerFireService`. It is **dual-role** (`.claude/rules/channels.md`): a timer with no satellite rings on its owner's channels, which only the agent holds, so the server is also a channel the agent dials (`channelEndpoints` id `timers`) that raises alerts and never a turn. `ChannelAlerts` (`Mcp.Hosting`, `AddChannelAlerts()`) is shared with Sugary's glucose alarm.

## A timer is its household's

- **Recorded from the verified call.** Every operation goes through `TimerFileSystem.For(FileSystemCaller)`: the household is the token's `tid`, the owner the person's `Account` (none for the guest), plus the turn's agent (`ArmedTimer.TenantId`/`Owner`/`AgentId`). A call acting for nobody — the server's own instance included — is refused every operation (`PermissionDenied`), never shown somebody's timers; so a test or seed arms through `For` (`EvalStack.ArmAsync`), never the bare instance.
- **Listed, read, searched, cancelled and dismissed by its household alone.** `ITimerStore` keys by `(TenantId, Id)`, and every call but the fire loop's claim names the household.
- **Shown on its household's clock.** A create's `note` states the ring time as a wall time plus zone name (`TimerFileSystem.RingsAt`); `status.json`'s `firesAt` is in the calling household's zone (`FileSystemCaller.Household`), so the model never adds the duration to the turn's timestamp itself.
- **A household with no voice server reaches no timers.** A non-home household without a `voice` credential gets its agents built without the timers server (`HouseholdAgents.RingsNowhere`, `SharedServers.WithheldBy(..., ringsNowhere)`); its countdowns become one-shot schedules (`MechanismScenarios.ACountdownWithoutAVoiceServer`). Not to be confused with a voice server whose household has no satellites: its no-target timers ring on channels. Minting the credential is announced (`.claude/rules/own-servers.md`), so the next turn has the timers.
- **Where it rings is the target's.** A `target` rings on the household's satellites through its own voice server; **no target rings on the owner's channels** (chat client with push, Telegram), which is how a household with no satellites has timers. A target whose satellites all answer `offline` (`AnnouncementOutcome.Offline`), or whose voice server is unreachable at the due second (`VoiceHubUnavailableException` — a suspended household's included, whose alert the agent then refuses like any fire), also rings on the owner's channels (user's decision, 2026-11-06; `TimerFireServiceTests`). The guest has no channels, so its no-target create is refused.
- **The satellites are read per call, never from the prompt.** `/timers/satellites.json` (`TimerPath.SatellitesFileName`) answers through the call's household. `timers_prompt` is fetched for nobody and cached once for every household, so `TimersSystemPrompt` must serve `TimerPrompt.Build([])` — a roster there would leak one household's satellites to all.

## Ringing on a satellite: the household's voice server, through the relay

- **The hub is the single source of truth.** Fire (`HttpInsistentAnnouncer`), dismiss (`HttpAlertDismisser`) and target validation (`HttpSatelliteCatalog`) go over HTTP to the voice hub; target resolution is **never** done in the timers process — the hub's `SatelliteRegistry` dual-keys rooms — so create-time validation matches fire-time routing (`ISatelliteCatalog`).
- **Bound per household.** `IHouseholdVoices.For(tenantId)` (`HttpHouseholdVoices`) binds the three adapters to one household, named on each request in `X-Ziggurat-Household` (`RelayedCallProtocol.HouseholdHeader`): the mount uses its caller's household, the fire loop the timer's record. Keep `IInsistentAnnouncer`, `IAlertDismisser` and `ISatelliteCatalog` household-free — voice implements the first in process and Home Assistant dials its own hub — which is why the binding is a factory.
- **Reached only through the relay.** A household's voice server has no deployment address: `VOICEHUB__BASEURL` is the agent's relay (`http://agent:8080/internal/relay/voice/`), which forwards to the named household's server alone — a request naming none is refused (403), never sent to the home's — (`.claude/rules/tunnel-forwarding.md`). The timers client presents its own name (`Mcp:Audience`) and `/mcp` credential (`RelayedCallCredential`) and holds no announce token; the relay presents the household's tunnel credential, which `VoiceHubAuth` accepts while tunnelled. `VoiceHubAuth`'s `BindToLoopbackOnly` must stay false, since the timers server is another container.
- **Fail closed at create.** An unreachable voice server (the relay's 502, read by `VoiceHubHttp`) fails a targeted create `TransientDependency` and arms nothing; an unresolvable target is refused with the roster. `./dismiss` still stops what rings on channels.

## Ringing on the owner's channels: through the agent

A timer with no target rings as an **insistent alert** that flows through the agent as a fire does and ends as an approval does (`AlertRelayTests`):

1. **Raised.** `TimerFireService` calls `ChannelAlerts.RingAsync` (`IChannelAlerts`), emitting `ChannelInboxItemKind.Alert`. Gate-on-live, as scheduling's: an alert no agent was polling to take is logged as not rung, never kept to ring late.
2. **Offered.** The agent's one `AlertRelay` (`Domain/Monitor`; ringing table `IRingingAlerts`, Infrastructure `RingingAlerts`) offers it with `offer_alert` on every other connected channel advertising it, signed for nobody, naming the owner.
3. **Settled.** The first `dismissed` sends `settle_alert` (`dismissed: true`) to every other channel, the timers server included, and calls their offers off; `./dismiss` stops it at the origin (`ChannelAlerts.StopAllAsync`) and emits `AlertStopped`, on which the relay settles every channel still showing it; an alert shown nowhere is settled at its origin at once; one nobody dismisses rings out after `AlertRelay.RingWindow` and is settled everywhere with `dismissed: false`.

**Where it shows.** Chat client (`AlertService`): `OnAlertOffered` to the owner's `account:{userId}` group in any space plus a Web Push always (unlike an approval offer), `DismissAlert` from any of their clients, `GetOfferedAlerts` for a client catching up. Telegram (`OfferAlertTool`, `ApprovalCallbackRouter.SettleAlertAsync`): the owner's private chat by the setting agent's bot, else the first other bot they opened; one Dismiss button counting for the owner alone; every copy edited when it ends.

**Trust:** the owner an alert names is the timers server's word, taken from the verified arming call; the agent believes a deployment server on its credential, as for an approval's asker. An alert shows text and acts as nobody, and a dismissal by anyone but the owner answers nothing (`AlertService`, `OfferAlertTool`).

`timers_prompt` keeps only which mechanism a moment is and that ringing is silenced through `/timers`; the doing rules belong in the `countdown-timers` skill (`CountdownTimersSkill`, `docs/adr/0039`).
