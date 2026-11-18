# 13: A glucose alarm rings on satellites or channels

**What to build:** Sugary as a relay caller (`RelayedCalls`, compose `VOICEHUB__BASEURL`, `MCP__AUDIENCE`), `IHouseholdVoices` wired in Sugary; the announce effect rung with `AnnounceKind.Alarm` and its insistent options. Move `ChannelAlerts`/`settle_alert` out of McpServerTimers into shared hosting keyed by an alert request (tenant, owner, text, kind, agent) instead of `ArmedTimer`; timers keep their behaviour. Sugary rings a channel alert on the subject's (and writer's, if different) channels when there is no target, every satellite is offline, or the hub is unavailable. Verify the hub's dismiss stops an alarm rung by Sugary. Spec: "Announce effect".

**Blocked by:** 11

Status: done

- [x] A watch's announce reaches the household's hub through the relay; a request for another household is refused.
- [x] Offline/unavailable falls back to a channel alert that `AlertRelay` offers and a Dismiss settles.
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


**The announce effect (2026-11-18).** `GlucoseAnnounce : IGlucoseAnnounceEffect`
(`McpServerSugary/Glucose/GlucoseAnnounce.cs`; the seam `IGlucoseAnnounceEffect.cs` is ticket 12's, written
here with the identical signature so the merge reconciles it). Registered in `ConfigModule` beside
`AddHouseholdVoices` (`.AddSingleton<IGlucoseAnnounceEffect, GlucoseAnnounce>()`). **On merging with 12:**
remove its `UnwiredGlucoseAnnounce` registration — whichever registration comes last wins, and the
integration tests resolve the seam from Sugary's own container, so they go red if the no-op wins.

- **Rung** through `IHouseholdVoices.For(watch.Subject.TenantId)` — the subject's household, never anyone
  else's — as `AnnounceKind.Alarm`, the target read from the effect's JSON as `SugaryWatchFileSystem`
  validates it (case-insensitive `AnnounceTarget`). Awaits the hub's answer only; the hub runs the ring
  detached, so nothing waits for an acknowledgement.
- **Text is plain**, said as written — no placeholders. A home watch's announce is "a fixed sentence"
  and ticket 14's skill can say the same of both; the reading, trend and detail are a `prompt` effect's to
  tell (the skill's usual pairing). `{value}` would need her unit (a store read in the fire path) and an
  answer for sensor triggers that have no reading.
- **Insistent**: left out, it rings as a timer does (`GapSeconds 10, MaxRepeats 12, RampStartPercent 100` —
  heard at once, two minutes, the same as `AlertRelay.RingWindow`), never `null`: the hub takes a null
  `Insistent` as a one-shot announcement that `ActiveAlertRegistry` never holds, so nothing could stop it.
  Given, it is hers as written and each field it leaves out is the hub's alarm default — so `{}` is the
  hub's defaults (with its wake-up ramp), as a home watch's `{}` is. One that does not deserialize rings as
  a timer rather than not at all (logged).
- **Channels** when there is no target, every satellite the hub answers is `offline` (`RangNowhere`, the
  timers' rule), or `VoiceHubUnavailableException`: a `ChannelAlertRequest(person, text, Alarm,
  watch.AgentId)` for the subject and, when different, for the writer (`Account(subject.TenantId,
  WriterUserId)`), both rung whatever the other's came to.
- **Answer**: true when the hub started it on at least one satellite, or any channel alert was taken by an
  agent. Any other failure (the hub answering wrongly — a satellite gone since the write, 404) is logged
  and false, so a `once` watch stays armed — channels throwing too, however the fire came to them; cancellation propagates.
- A channel alert Sugary rang ends by its Dismiss on a channel or rings out; `/timers ./dismiss` does not
  reach it (see above). A satellite alarm is stopped on the satellite or by `/timers ./dismiss`.

Tests: `Tests/Unit/McpServerSugary/GlucoseAnnounceTests.cs` (13: channels throwing — after no target or an unreachable hub — is false, never a throw; target → subject's household, Alarm;
insistent left out → timers' ring; given → hers; no target → channels, no hub asked; all offline → subject
and writer; one satellite started is enough; hub unreachable → channels; writer rung though the subject's
was taken; nothing taken → false; hub answering wrongly → false, no channels; cancellation propagates).
`RelayedCallsTests` (+2, Sugary's container through the real relay to a tunnelled voice server): the watch's
announce rings the kitchen's satellite and the hub holds it as an `Alarm` with its text (insistent left out);
with only another household's voice connected it rings none of theirs and answers false.
`SugaryChannelAlertsTests` (+3, real Sugary server, the agent's `McpChannelConnection`, two alert-showing
channel servers and a real `AlertRelay`): all satellites offline → offered on both channels to her, as Alarm
from jonas; her Dismiss on one calls off the other and settles at Sugary (`StopAllAsync` then empty); no
target and hub unreachable → offered to subject and writer. Green; two mutations (null `Insistent`, ringing
the subject only) each turned unit and integration tests red.
