# 13: A glucose alarm rings on satellites or channels

**What to build:** Sugary as a relay caller (`RelayedCalls`, compose `VOICEHUB__BASEURL`, `MCP__AUDIENCE`), `IHouseholdVoices` wired in Sugary; the announce effect rung with `AnnounceKind.Alarm` and its insistent options. Move `ChannelAlerts`/`settle_alert` out of McpServerTimers into shared hosting keyed by an alert request (tenant, owner, text, kind, agent) instead of `ArmedTimer`; timers keep their behaviour. Sugary rings a channel alert on the subject's (and writer's, if different) channels when there is no target, every satellite is offline, or the hub is unavailable. Verify the hub's dismiss stops an alarm rung by Sugary. Spec: "Announce effect".

**Blocked by:** 11

Status: ready-for-agent

- [ ] A watch's announce reaches the household's hub through the relay; a request for another household is refused.
- [ ] Offline/unavailable falls back to a channel alert that `AlertRelay` offers and a Dismiss settles.
- [x] Timer tests unchanged and green after the move.

## Comments

**Prefactor done (2026-11-18, edeaf96be).** No glucose-watch behaviour yet; ticket 11's engine calls this.

- `ChannelAlerts` and its `settle_alert` (`ChannelAlertSettleTool`) live in `Mcp.Hosting`, registered by
  `IMcpServerBuilder.AddChannelAlerts()` after `AddChannelServer` (it throws otherwise).
  `IChannelAlerts.RingAsync` takes a `ChannelAlertRequest(Account Owner, string Text, AnnounceKind Kind,
  string? AgentId)` (`Domain/DTOs/Channel/ChannelAlert.cs`); its household is `Owner.TenantId`, so there is
  no separate tenant field to disagree with the owner. The timers server maps a timer to it in
  `TimerFireService` (no owner: not rung; text `<id> timer` when none), so timers behave as before. The
  timer tests' three `IChannelAlerts` fakes changed signature; `RecordingChannels` now records the text,
  so its assertions read `"pasta timer"` where they read the id `"pasta"`.
- No new `AnnounceKind`: the chat client's `AlertModal` and push title say "Alarm" for `Alarm`, Telegram
  renders no kind, and a new value would also reach the hub's tone and replay logic. A glucose alarm is
  `AnnounceKind.Alarm`, as the spec says.
- Sugary is a relay caller: `RelayedCalls` lets `mcp-sugary` reach `voice` under `/api/voice`; Sugary
  wires `IHouseholdVoices` through the new shared `AddHouseholdVoices` (`Infrastructure/Clients/Voice`,
  which timers uses too) from `SugarySettings.VoiceHub.BaseUrl` (default and compose
  `VOICEHUB__BASEURL: http://agent:8080/internal/relay/voice/`), `Mcp.Audience` and `Mcp.SharedSecret`,
  with no announce token. Compose already had `MCP__AUDIENCE: mcp-sugary` and the agent's
  `MCP__SECRETS__mcp-sugary`; `DeploymentConformanceTests.ARelayedCaller_ReachesTheHouseholdsVoiceThroughTheRelayAlone`
  pins all three for both callers. Sugary hosts `AddChannelAlerts()` on its channel server.
- **The hub's dismiss is per household, not per kind or caller.** `/api/voice/dismiss` (`DismissEndpoint`)
  calls `ActiveAlertRegistry.DismissAll()`, which acknowledges every handle registered on that voice server
  whatever its kind or who announced it; the registry has no household key — it is per household because
  each household runs its own voice server and the relay forwards to the named household's alone. So
  `/timers ./dismiss` stops a glucose alarm Sugary rang on a satellite (pinned:
  `RelayedCallsTests.SugarysAlarm_RingsOnTheHouseholdsVoiceServerThroughTheRelay_AndTheTimersDismissStopsIt`,
  stdout `alarm "Glucose is low"`). The converse does **not** hold for channel alerts: `./dismiss` stops
  only the timers server's own `ChannelAlerts` table, so a glucose alarm ringing on channels ends by its
  Dismiss on a channel, or rings out after `AlertRelay.RingWindow` — never by `/timers ./dismiss`.
- Left for this ticket proper: the offline/unavailable fallback (every satellite `offline`, or
  `VoiceHubUnavailableException`, rings on channels) is still private to `TimerFireService`; Sugary's
  announce effect writes its own, ringing on the subject's and the writer's channels.

