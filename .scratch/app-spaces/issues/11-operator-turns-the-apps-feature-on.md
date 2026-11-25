# 11: Operator turns the apps feature on

Spec: `.scratch/app-spaces/spec.md` ("The apps server").

**What to build:** The operator turns the `apps` feature on or off for a household from the dashboard's household page. The owners' features page shows it but cannot change it. With the feature on, the household's own agents are offered `/apps` from their next turn.

**Blocked by:** 01

**Status:** ready-for-agent

- [x] The operator's household page sets and clears `apps`.
- [x] An owner's save of the features section never changes `apps`, even when it sends a value.
- [x] A household without the setting reads it as off.
- [x] Covered by the operator and owner admin integration tests.

## Comments

**Landed (branch `app-spaces`).** `PUT /accounts/admin/operator/households/{householdId}/features/apps`
(`AdminProtocol.AppsFeature`, `HouseholdAppsFeatureAdmin`) takes `HouseholdAppsFeature { on }` and sets the
features section's `apps` alone, through `HouseholdConfigStore.SaveAsync` with `FeaturesSchema`, so it is
announced and the household's agents are offered `/apps` (or lose it) from their next turn. It is behind
the operator gate like every `/operator` route, answers `AdminSaved<HouseholdAppsFeature>` (with
`AdminSections.NotToldWarning` when the agent could not be told), is 404 for no such household and 400 for
no body, and is recorded on the target household only when the value changed ("Turned the apps feature
on/off"). `HouseholdListEntry` gained `Apps` (a household with no features row, or one saved before the
key existed, reads off). The dashboard's `/households` has an Apps column with "Turn on"/"Turn off"
(`HouseholdsEffect.SetAppsFeatureAsync`), re-reading the list after either. The owners' features page
shows `apps` as a disabled toggle (`FeatureHints.Apps`: only the deployment's operator turns it on or
off) and sends it back as loaded; the server still strips it (ticket 01).

**Choices:**
- *The operator's route takes the household's turn* (`OneChangeAtATime.InTurnAsync`, keyed by the path's
  household). An owner's features save reads the section and saves it whole with `apps` as read, so an
  unqueued operator change landing in between was undone. The operator's other routes stay unqueued.
- *The home may have apps* (no `IsHome` refusal, unlike suspension).
- *No confirmation* before turning it off: nothing is deleted, and app spaces are left as they are.

**Tests:** `HouseholdAppsFeatureAdminTests` (on, offered at `HouseholdAgentSource` over the real reader
and Redis; off; unchanged records nothing; owners' features kept; off by default on the list and the
owners' GET; 404; 400; owner and member 403; waits for the household's change in flight, held by an
owner's key probe), `HouseholdsEffectTests` (dashboard switch, warning, refusal), a `FeatureHintsTests`
row. The owner side is ticket 01's `HouseholdFeaturesAdminTests` rows; the withhold rule is
`HouseholdAgentsTests.TheAppsServer_IsWithheldUntilTheAppsFeatureIsOn`.

**Not done:** no E2E for the dashboard column or the owners' read-only row (not run here); the page
markup was built, not seen in a browser. The home's seeded definition agents still reach `/apps` only once
the operator adds `http://mcp-apps:8080/mcp` to their definitions (ticket 01's note): turning the feature
on for the home offers it to the jonas template alone.
