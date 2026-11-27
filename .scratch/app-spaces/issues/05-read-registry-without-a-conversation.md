# 05: Prefactor: a read registry without a conversation

Spec: `.scratch/app-spaces/spec.md` ("The app read API (the agent)"; Further Notes).

**What to build:** Today every VFS registry is built inside an agent session. Extract the build so the agent can make a registry for (household, agent definition, person) outside any conversation. It must dial the definition's servers withheld by features and own-server state exactly as a turn's are, and sign each call as that person of that household. Nothing outside the agent changes and turns behave the same. This makes the app read API (06) an easy change.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [x] A turn's session builds its registry through the extracted builder, and existing VFS and session tests stay green unchanged.
- [x] A registry built for a person outside a conversation mounts the same mounts a turn of that agent would, with the same features and own servers withheld.
- [x] Calls through it are signed for that person and household, and a per-person mount (`/sugary`) answers that person's rows.
- [x] A household server going offline or reconnecting is seen at the next build, as a turn sees it.

## Comments

**Landed (branch `app-spaces`).** The build a turn's session goes through is `SessionReach`
(`Infrastructure/Agents/SessionReach.cs`): `ResolveAsync` is the lookup (own servers withheld by what the
household has not set up, asked each time, then outposts composed) answering a `ServerReach` that compares
with a later one; `BuildAsync(reach, SessionShape)` dials and mounts through `ThreadSession` and declares
unreachable machines. `McpAgent.GetOrCreateSessionAsync` now reconciles each turn over it; behaviour is
unchanged (the refactor commit ran 1,239 VFS, session, agent, own-server and outpost tests green before
any new test).

The entry point for 06 is `AgentReadRegistries` (registered in the agent's `InjectorModule`):

```csharp
public Task<ReadRegistry> BuildAsync(IdentityClaims viewer, string agentId, CancellationToken ct)
```

`viewer` is the identity the agent verified (06: `AssertionVerifier.VerifyTurnAsync(assertion,
AppChannels.Apps)`'s `TurnAdmission.Admitted.Identity`); the household is its `tid`, never a second
argument. `ReadRegistry` (`IAsyncDisposable`, owns the dialled clients) exposes `Registry` (the raw
`IVirtualFileSystemRegistry`; empty for an agent with no filesystem tools), `Caller` (the
`ConversationContext` to bind with — `VfsCall`'s `caller` — carrying the person as `Identity`, conversation
`ReadRegistry.NoConversation`), `Offline` (own servers named and not reached) and `IsStaleAsync` (for 06's
cache: the household's `AsBuilt` of the agent compared as an open conversation's agent compares it — a
feature turned off, the agent removed — and its servers compared as a turn's start compares them).

Tests: `AgentReadRegistriesTests` (9 cases, over the wire against in-process servers): Her and Him each
read their own `/sugary` meals through the real Sugary server, signed by the agent's call tokens with
`chn` `apps`; mount parity with a real turn of the same agent (its prompt's mounts, through the real
factory and `FakeOpenRouter`) for an own server set up and connected, set up and offline, and never set
up, with a shared server withheld by a feature in every case; a server going offline and coming back seen
by `IsStaleAsync` and the next build; a feature turned off or the agent removed after the build seen by
`IsStaleAsync`, another agent's change not; no filesystem tools mounts nothing; an agent the household lacks
and a person's registered agent refused as `AgentNotFoundException` naming it; the guest and nobody
refused. `InjectorModuleTests.ConfigureAgents_BuildsReadRegistries`. Contracts in
`.claude/rules/virtual-filesystem.md` ("A registry may be read outside any conversation") and
`own-servers.md`.

**Decisions:**
- *Outposts are out*, by construction: `AgentReadRegistries` takes no `IOutposts` and builds with
  `usesOutposts: false` whatever the definition says. A machine is somebody's own computer, reached for the
  person at it; an app reads household data. No test pins it, since nothing could inject a machine.
- *Only a person reads*: the guest and nobody are refused (`ArgumentException`), and the identity is
  re-derived as `IdentityClaims.Person` so no envelope or `sid` rides the `_meta`.
- *The household's own agents alone* (`HouseholdAgents.Find`), so a viewer's registered agent of the same
  id is never reached.
- *A turn's gates kept*: `noTools` and the `filesystem` feature decide whether anything is mounted, as for
  a turn.

**Left to 06:** the cache, the read-only and `reads` gates, the whitelist's approval gate, the byte
ceiling, and whether a suspended household or a disabled member reads (the builder checks neither; a turn
checks both in `ConversationGroup` before anything is built, so 06's route should ask `IAccountStanding`
and the household's suspension before building). The red-first evidence is honest only for the Sugary
slice, the DI registration and the stale-agent case: the builder was written whole in the first slice, so the later slices
passed on first run; each was mutation-checked instead (removing the own-server withholding, the stale
check, the filesystem gate and the person check each failed exactly its test).
Not tested on the read path: the `noTools` gate (only the `filesystem` gate is), and a reconnect as a new
tunnel generation, which rests on `ServerState`'s comparison shared with turns (in-process servers have
no generations).
