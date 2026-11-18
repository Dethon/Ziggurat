# 13: A glucose alarm rings on satellites or channels

**What to build:** Sugary as a relay caller (`RelayedCalls`, compose `VOICEHUB__BASEURL`, `MCP__AUDIENCE`), `IHouseholdVoices` wired in Sugary; the announce effect rung with `AnnounceKind.Alarm` and its insistent options. Move `ChannelAlerts`/`settle_alert` out of McpServerTimers into shared hosting keyed by an alert request (tenant, owner, text, kind, agent) instead of `ArmedTimer`; timers keep their behaviour. Sugary rings a channel alert on the subject's (and writer's, if different) channels when there is no target, every satellite is offline, or the hub is unavailable. Verify the hub's dismiss stops an alarm rung by Sugary. Spec: "Announce effect".

**Blocked by:** 11

Status: ready-for-agent

- [ ] A watch's announce reaches the household's hub through the relay; a request for another household is refused.
- [ ] Offline/unavailable falls back to a channel alert that `AlertRelay` offers and a Dismiss settles.
- [ ] Timer tests unchanged and green after the move.
