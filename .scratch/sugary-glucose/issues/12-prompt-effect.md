# 12: A glucose watch's prompt reaches the agent

**What to build:** `DelegatedWrites` signs for writes to `sugary-watches`; Sugary keeps the delegation with the watch and serves `delegations_list`/`delegations_replace` (`AddDelegationStore`); the verifier gains the delegated kind. A fired prompt effect is composed by `FirePlanning` (principal = `userId`, household, Watch origin with the id, report beside the prompt, `deliverTo`) and emitted on Sugary's channel. A fire no agent took keeps a `once` watch armed and is logged. Spec: "Prompt effect".

**Blocked by:** 11

Status: done

- [x] Over a real inbox: a fire arrives with the delegation, verifies, and runs as the watch's person.
- [x] A watch written by a turn for nobody fires for nobody; redelegation replaces the stored token.
- [x] `once` is spent only by a fire an agent took.

## Comments

Done in 5320d4416.

- **Kind: `watch`, reused, with the home watch's description** (`DelegatedWatch`: agent, prompts as written,
  delivery as the write resolved it). The spec fixes the fire's origin as Watch, `DelegatedObject.KindOf` maps
  an origin kind to one delegated kind, and the verifier's Watch check (id, agent, the words one of the
  prompts, reply targets) is exactly what a glucose prompt fire does; a new kind would have meant a new
  `MessageOriginKind` through the agent, the eval and the clients. `WatchDelegation.Describe` is now public
  and `GlucoseWatchDelegation` (`Domain/Tools/Sugary/Watches/`) builds on it (`Of(StoredGlucoseWatch)`,
  `OfWrite(path, text, existing, agentId, origin)`), so both ends describe the two kinds by one function.
  A home watch and a glucose watch may share an id (ticket 10's note); a delegation is kept by the server
  that stored it, so one is never the other's to present, and a token lifted between them would ask the
  same words of the same agent delivered to the same place.
- **Delegation at write.** `DelegatedWrites` delegates `sugary-watches` `<id>/watch.json` and reads first on
  an overwrite, as for `ha-watches`. `SugaryWatchFileSystem.For` keeps `caller.Delegation`; every write
  replaces the stored one (ticket 10 kept it across writes) with what the call carried, kept only when it
  names this watch and the writer (`GlucoseWatchDelegation.KeptFor`, `Delegation.Unverified`), else none.
- **"Written by nobody" cannot happen through the mount**: a turn acting for nobody is refused every write
  (ticket 10). The reachable form is a write carrying no delegation (a call not through the agent's
  filesystem), which leaves the watch nobody's: its fire carries none and is admitted `Undelegated(null)`,
  ordinary, not a refusal. That is what the test pins.
- **Found on the way and fixed: a person's edit of a home watch fired for nobody.** The watch mounts number
  the lines of a read (`HaReadResult.Numbered`), and `McpFileSystemBackend` signed an edit or a replace over
  that read — which parsed as no watch, so no delegation. `DelegatedWrites.FileText` (`HaReadResult.Unnumbered`)
  takes the numbers off for both watch mounts. `WatchDelegationTests.AWatchAPersonEdited_FiresAsThem_…` was red
  without it.
- **`userId`**: the writer, the subject, or what the watch already said (the file reads back with its
  `userId`, so the subject editing a watch a grantee set up writes the grantee's id back), else
  `invalid_argument` naming `userId`. Defaults to the writer. It is the fire's `Principal`/`UserId`, a label:
  the agent runs the turn as the delegation's person, the last writer.
- **Effects** (`GlucoseWatchEffects`, replacing `LoggedGlucoseWatchEffects`): in order; a prompt composed by
  `GlucoseWatchFire` through `FirePlanning.Compose` — content the watch's prompt, `FireReport` the
  `GlucoseFireReport` (watch, what fired, the reading in mg/dL and mmol/L, trend, rate, forecast, since, sensor
  end, when on the zone the engine judged on, the unit she reads, "your reply is what is delivered"),
  principal `Principal("sugary", userId, Account(subject's tenant, userId))`, `HouseholdId` the subject's
  tenant, origin `Watch` with id and name, sender `ServiceSenders.GlucoseWatch` ("A glucose watch fired" in
  WebChat's `ServiceNotice`), the delegation with the watch's description — and emitted gate-on-live, counted
  when an agent took it; nothing waits for the turn. Conversation id `glucose-watch-<id>-<trigger>-<effect>-<ms>`,
  since two prompts of one fire read one clock. `SugarySettings.Delivery` reads `Domain/delivery.json`.
- **The announce seam, for ticket 13** (`McpServerSugary/Glucose/GlucoseWatchEffects.cs`):

  ```csharp
  public interface IGlucoseAnnounceEffect
  {
      Task<bool> RingAsync(FiredGlucoseWatch watch, GlucoseAnnounceEffect effect, GlucoseTriggerFire report, CancellationToken ct);
  }
  ```

  Registered as `UnwiredGlucoseAnnounce` (logs, answers false) in `ConfigModule`; replace that registration.
  True counts as delivered; a throw is logged and the effects after it still run.
  `FiredGlucoseWatch` keeps its positional shape and gained init properties `Zone` (the engine's judging zone)
  and `Unit` (her link's, mg/dL without one), which the engine sets.
- **Delegation store** (`GlucoseWatchDelegationStore`, `.AddDelegationStore<…>()` on Sugary): lists every
  household's watches (factory context, `IgnoreQueryFilters`, added to `sugary.md`'s list) with the creator as
  the writer offered for one with none; replace rewrites the delegation column alone (states and spent kept)
  and refuses an id two households hold.
- **Step 4 (the household's zone through `CallHousehold.OfFire`) not done**: the accounts service takes a
  delegation as household proof only of kind `schedule` and only from `mcp-scheduling`
  (`HouseholdProofs.DelegationAsync`, `InternalCallers`), so it needs an accounts-side change (accept a
  `watch` delegation from `mcp-sugary`). The engine keeps the phones' saved zone.

- **Open, not built: a household that turned Sugary off.** `ConversationGroup.FeatureRefusalAsync` refuses a
  person's turn on channel `sugary` for such a household, but a fire (`AdmitFireAsync`) is not asked it, so a
  glucose watch's prompt of that household still runs (its agent without `mcp-sugary`). Nothing else on the
  agent keys on the `sugary` channel alone (no `audience-channels: sugary` section; Sugary's `send_reply` drops
  a chunk for a turn key it did not open, so a fire delivering nowhere on Sugary is harmless). Whether the
  engine should stop judging, or the agent refuse the fire, is a decision for whoever picks it up.

Tests: `GlucoseWatchDelegationTests` (integration, the real Sugary server written through the agent's
`McpFileSystemBackend` and signing transport, fired through the server's own effects and read off the agent's
`McpChannelConnection`: a person's write, edit and delivery-keeping replace fire as them; a write with no
delegation fires for nobody; a forged one is refused `UnknownKey`; redelegate after a lost key replaces the
stored token and fires as the writer; a `once` watch through the real engine stays armed while no agent polls
and is spent once one does), `GlucoseWatchEffectsTests` (real inbox: order, counting, composition, gate-on-live,
a failing announce contained), `GlucoseFireReportTests`, `DelegatedWritesTests` rows (sugary-watches, `FileText`),
`SugaryWatchFileSystemTests` (delegation kept/cleared/not kept for another watch or person, `userId` rules),
`SugaryWatchFileSystemCallerViewTests` (the delegation is a view's), `ServiceNoticeTests`, and the home's
`WatchDelegationTests` edit case. Red: the tests compiled red against missing types; afterwards each guard was
proven by mutation (numbers left on the read: 3 edit/replace tests red, home and Sugary; `KeptFor` keeping any
token, the `userId` check off, the old keep-on-replace, a prompt counted delivered when nobody listened: 6 red).
`Tests.Unit` plus the glucose-watch and both delegation classes 7643/7643 at HEAD after the hook's format; integration Sugary/delegation/scheduling/watch/conformance/contract 788 passed; eval
harness, redelegation and thread-session 498 passed. Eval not run.

**Follow-up (2026-11-18).** A prompt nobody took (gate-on-live, no agent polling) no longer spends the
episode: the engine tries the fire again at its next judgement while the condition holds (ticket 11's
follow-up). And the agent side is now proven: `GlucoseWatchAgentFireTests` drives a glucose fire from the
real Sugary engine through the agent's channel connection into `ChatMonitor` with the shipped
`agentDefaults` (`sugary` → the no-tools estimator) and the real `MultiAgentFactory` on `FakeOpenRouter`:
the fire names the watch's agent (`Fire.AgentId` = `StoredGlucoseWatch.AgentId`, which `RouteToAgentAsync`
keeps), so it runs as Jonas, acts for her (delegated), and its reply lands on the `deliverTo` channel and
nothing on Sugary's. Nothing needed fixing; red by mutation with `AgentId` blanked (the estimator answered)
and `DeliverTo` dropped.
