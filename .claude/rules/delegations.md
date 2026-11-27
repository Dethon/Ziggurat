---
paths:
  - "Domain/Security/Signing/Delegation.cs"
  - "Domain/Security/DelegationVerifier.cs"
  - "Domain/Security/DelegatedObject.cs"
  - "Domain/Security/DelegatedWrites.cs"
  - "Domain/Contracts/IDelegationStore.cs"
  - "Domain/DTOs/Channel/FireDelegation.cs"
  - "Domain/Channels/FireReports.cs"
  - "Domain/Monitor/ConversationGroup.cs"
  - "Domain/Monitor/ChatMonitor.cs"
  - "Domain/Tools/Scheduling/Vfs/ScheduleDelegation.cs"
  - "Domain/Tools/HomeAssistant/Vfs/WatchDelegation.cs"
  - "Domain/Tools/Sugary/Watches/GlucoseWatchDelegation.cs"
  - "Domain/Tools/Web/LoginDelegation.cs"
  - "Mcp.Hosting/DelegationStoreTools.cs"
  - "Mcp.Hosting/CallTurn.cs"
  - "Infrastructure/Agents/Mcp/SigningClientTransport.cs"
  - "Infrastructure/Agents/Mcp/CallTokens.cs"
  - "Infrastructure/Agents/Mcp/McpFileSystemBackend.cs"
  - "Infrastructure/Agents/Mcp/Redelegation.cs"
  - "Agent/Commands/AgentCommand.cs"
  - "Agent/Commands/CommandServers.cs"
  - "McpServerScheduling/Services/ScheduleDelegationStore.cs"
  - "McpServerHomeAssistant/Services/WatchDelegationStore.cs"
  - "McpServerHomeAssistant/Services/WatchFiredEndpoint.cs"
  - "McpServerHomeAssistant/Services/WatchFire.cs"
  - "McpServerSugary/Glucose/GlucoseWatchDelegationStore.cs"
  - "McpServerSugary/Glucose/GlucoseWatchFire.cs"
  - "McpServerWebSearch/Handoff/LoginWake.cs"
  - "Tests/TestDelegations.cs"
---

# Service identity: delegations

See also: `service-identity.md` (the token, keys, call tokens — a delegation rides beside one), `person-assertions.md` (turn admission and "Disabled members", which fires share), `identity-smoke.md` (the smoke tier).

## Deferred turns: delegations

A schedule's fire, a login wake and a watch's fire act for a person only because that person's turn
created or last rewrote the object, and only on the agent's own word: a **delegation**, signed by the
agent's key while that turn's call wrote the object. A server stores one and hands it back; it cannot
make one, move one onto another object, or fire a changed object as its writer.

**The token** is `Delegation` (`Domain/Security/Signing`), an `IdentityClaims` with three claims of its
own: `knd` (`schedule`, `watch`, `login` — `DelegatedObject`'s constants), `oid` (the schedule's id, the
watch's id, the asking conversation's id) and `dig`, base64url SHA-256 of the object's **description**:
canonical JSON of what it does, computed on both ends by one Domain function per kind
(`ScheduleDelegation`, `WatchDelegation`, `LoginDelegation`):

| Kind | Description (`DelegatedObject.cs`) | What the fire must match |
|---|---|---|
| schedule | agent, prompt, cron, runAt (unix seconds, UTC), deliverTo as written | `Origin.ScheduleId` = oid, `AgentId`, `Content` = prompt, `ReplyTo` = the deliverTo resolved against the delivery default |
| watch | the agent that runs it, the prompt effects' prompts as written, deliverTo as the write resolved it | `Origin.WatchId` = oid, `AgentId`, `Content` = one of the prompts exactly, `ReplyTo` as above |
| login | the asking turn's agent, conversation and origin, and the site (`Uri.Host` of the url `web_login` was called with) | `AgentId`; then either the asking conversation answering at its origin with `Content` = `LoginDelegation.DoneText(site)` (Done), or one fresh chat-client conversation (`[signalr, null]`) with `Content` = `LoginDelegation.LinkText(site)` (a voice login's link) |

**What a fire asks is the object's own words; everything else is its report.** `Content` is what a
delegation covers; anything a server learned at fire time rides beside it as
`ChannelMessageNotification.FireReport` (`Fire.Report`), framed around `Content` by the conversation
group only after the check (`FireReports.Frame`: a `<fire-report …>` block, information not
instructions, any closing tag inside escaped).

- A **watch**: the home renders a prompt's Jinja (so the words it posts need not be the signed ones) and
  anything holding the announce token can post to `WatchFiredEndpoint`, so the callback names which
  prompt it rendered (`'promptIndex': N`, a literal `HaWatchAutomation` writes into every
  prompt callback) and the server asks the prompt **as the home keeps it**, looked up with the
  delegation; what fired and the rendered prompt are the report, so a forged post's words are only ever
  report. A post naming no prompt of the watch, or a home that cannot be asked, fires the rendered prompt
  with no delegation, as nobody.
- The agent that runs a watch is the creating turn's (`HaWatchMetadata.AgentId`, kept by a rewrite); the
  agent side describes a write with the writing turn's agent (`DelegatedWrites.Of`), so another agent's
  rewrite describes what the home will not run and its fires act for nobody.
- A **login**: both wakes say `LoginDelegation.DoneText`/`LinkText`, spelled once for the browse server
  and the verifier; the voice login's one-time link, minted by the browse server, is the link turn's
  report.

`iss` the agent, `aud` `TokenAudiences.Delegation`, `sub`/`tid`/`chn`/`roles` the person's (only a person
is delegated — the guest and nobody delegate nothing), `exp` **iat + 100 years** for a schedule or watch
(`Delegation.ObjectLifetime`: deletion, a rewrite or retiring the key ends it) and
**iat + `Delegations:HandoffLifetime`** (1 hour, covering the handoff tab) for a login.

**Minted where the write leaves.** `SigningClientTransport` signs one for a call acting for a person,
under `_meta["com.herfluffness/delegation"]` (`ChannelProtocol.DelegationMetaKey`), beside the call
token, for exactly what the call writes:

- A filesystem write states its object under the agent-only `ChannelProtocol.DelegateMetaKey`
  (`McpFileSystemBackend`, through `DelegatedWrites`), named with the writing turn's agent: a create
  from its content (schedules, `ha-watches`, `sugary-watches` — both `/watchers`); an edit by reading
  the file first and laying the edits over it as the mount does; a schedule move by reading its file
  under the destination's name; a watch replace by reading the watch whose delivery it keeps.
- `web_login` is a login handoff by its name, described off the call's own context and its `url`.
- Whom a write acts for is the caller bound to the backend, never the invocation in flight
  (`ToolCaller.Resolve`, `FileSystemResolution.As` over `ICallerBoundBackend`;
  `.claude/rules/virtual-filesystem.md`, "A tool resolves as its caller"); the exec bridge binds its
  command's caller. The backend as discovered acts for nobody and stamps no `_meta`.
- The transport removes both keys from every call and writes the delegation only for a person, so
  neither a model nor a caller can put one there.
- **A built app's ask delegates nothing** (an identity on the `apps` channel, `DelegatedWrites.MayBeLeftBy`):
  the transport signs it none, the mount proxy refuses its write a later turn would act on before anything
  is sent (`McpFileSystemBackend`), and the approval client refuses such a call as asked (`StartsLaterTurns`;
  `channels.md`, the app-turn bullet).
- A runAt with no offset is read in the agent's zone (its `TimeProvider`'s `LocalTimeZone`, handed to
  each mount's backend as `ThreadSession`'s `localZone`), which compose's shared `TZ` makes the
  scheduling server's; zones that disagree make such a one-shot fire for nobody.

**Stored with the object, replaced by every write.** The server reads it with `call.Delegation`
(`CallTurn`), never verifying it; deleting the object deletes it:

- `ScheduleFileSystem` on `Schedule.Delegation`, for every create, edit and move (a write carrying none
  clears it).
- `HaFileSystem` in `HaWatchMetadata.Delegation` (through `FileSystemCaller.Delegation`).
- `LoginHandoffs` on the `LiveHandoff`.
- `SugaryWatchFileSystem` on `StoredGlucoseWatch.Delegation`, kept only when it names that watch and the
  writer.
- A watch mount numbers its read lines, so the read before an edit or replace is taken without them
  (`DelegatedWrites.FileText`), or it parses as no watch and fires for nobody.
- `Schedule.CreatedBy` still records who last wrote it — what redelegate offers for a schedule with none
  — and is never what a fire acts as.

**Fired with what the object does now.** `FireDelegation(Token, Object)` rides
`ChannelMessageNotification.Delegation`: the scheduler's from the stored schedule
(`ScheduleFirePlanner`); the watch callback's looked up from the home at fire time
(`WatchFiredEndpoint`, `HaWatches.GetAsync`; a home it cannot ask fires for nobody); a glucose watch's
from Sugary's stored watch (`GlucoseWatchFire`, described by `GlucoseWatchDelegation` with the home
watch's shape and kind, since the agent checks both the same); both login wakes' from the handoff. A
download's carries none.

**Checked as the fire arrives**, in `ConversationGroup.AdmitAsync` (`AdmitFireAsync`), by `DelegationVerifier` (the agent's
own `SigningKeyStore` — every key it still publishes — on the wall clock, and `Domain/delivery.json`,
which the agent reads too): signature, `aud`, a person, `knd` the fire's origin kind, `dig` the presented
description's digest, and the fire doing what the description says. It answers
`FireAdmission.Delegated(identity)` — the principal becomes `Principal(chn, sub, account)` and the
identity rides the turn as an assertion's does — or `Undelegated(refusal)`: the principal is dropped and
the turn runs as nobody. A refusal (unverifiable token, `NamesNoPerson`, `WrongKind`, `DigestMismatch`,
`NotThisFire`, `NotDelegable` for a download carrying one) is logged at warning and published as
`DelegationRefused`; a fire carrying none is ordinary and says nothing. A monitor built with no verifier
believes no delegation.

**Then the household decides whether it runs at all** — the check a person's turn gets
(`person-assertions.md`, "Disabled members"), and **refused, not run as nobody**, since a fire as nobody
still says its prompt on the home's agent:

- **A suspended household's fire starts nothing**, delegated or not: the households asked are the one
  its server keeps the object under (`ChannelMessage.HouseholdId`; a schedule's `TenantId` stamped by
  `ScheduleFirePlanner` through `Fire.HouseholdId`) and the delegation's person's. A watch's fire names
  none but arrives through its household's own Home Assistant server, whose household is asked too
  (`.claude/rules/own-servers.md`, "A household's channel server"). A login wake's household is its
  delegation's.
- **A disabled member's fire starts nothing** (`AccountDisabled` under their household). Nothing about
  the delegation is spent; re-enabled, it acts for them again.
- **A standing or a suspension that cannot be read** refuses the fire as `StandingUnreadable`.
- Only the integrity refusals above (`DigestMismatch`, `WrongKind`, a forged token, …) still run as
  nobody: they say the object is not what was signed, not that its household stopped it.

A refused fire was emitted all the same, so its server advances a cron and deletes a one-shot as if it
ran. Fire timing is not checked (nothing in a fire says when it was due), so the digest's timing only
catches a store edited under a server that still tells the truth about it. `run_now` fires a schedule as
its delegation says, whoever asked.

**Delegations hold against an honest server.** The signature bounds *what* a fire does as the person,
never *whether* or *when* it comes:

- A misbehaving server could keep a delegation for an object since deleted or overwritten and fire it
  again, at any time.
- An edit or move is signed over the content the server answered the agent's read with, so a lying
  server gets a delegation for its own words with the turn's edit on top; a create gives it nothing to
  lie about.
- The household a fire names is the server's word too, believed only to refuse: a lying server could
  have its own fire refused, or leave the household out of an undelegated one, which then runs as nobody
  as it could anyway.

**The commands** (`Agent/Commands/AgentCommand.cs`, System.CommandLine, detected in `Program` before
anything builds and run instead of the agent): `rotate-signing-key` (signs from the next call on; the
agent and every server pick it up by themselves), `redelegate [--yes]` and `retire-signing-keys [--force]`.

- The last two dial every `ChannelEndpoint` as the agent does — by URL, or by server name as every
  household's server of that name through the running agent's relay down its own tunnel, one not
  connected listed as not asked (`CommandServers`, `.claude/rules/own-servers.md`, "The key commands
  reach every household's servers the same way") — and use `delegations_list` / `delegations_replace`
  (`DelegationStoreTools`, `AddDelegationStore<T>`, over an `IDelegationStore`: the scheduling, Home
  Assistant and Sugary servers; behind the call-token gate, signed as nobody, and stripped from every
  model's tool list by `ThreadSession`). A replace naming another object is refused by the server.
- `Redelegation` leaves the current key's delegations alone, re-signs without asking one an earlier
  published key signed for the object as it is now, and shows the operator — signing a fresh one over
  what the object does now only on a yes — one it cannot believe (a key lost, an object changed since)
  and a schedule with none whose recorded writer names an account.
- Retirement refuses while a stored delegation is still an earlier key's, or a server could not be or was
  not asked (every server is still asked and listed first). The order is **rotate, then redelegate, then
  retire** (after the call-token lifetime plus skew).
- A provided key (`SIGNING__PRIVATEKEY`) is neither rotated nor retired and every verb exits 1.

**Deploying over existing objects.**

- Schedules written before delegations fire for nobody until the operator runs `redelegate` once, which
  offers each as the writer its `CreatedBy` names; a declined or unoffered one stays nobody's until a
  person's turn rewrites it.
- A watch with no delegation whose writer names no account is offered as the owner of the household
  whose server keeps it — the longest-standing enabled owner (`TunnelServerHolder.OwnerId`, through the
  agent's holders route to `Redelegation.Server.Owner`) — the migration's answer for a schedule nobody
  can be traced for (user's decision, 2026-11-04); declined, it fires for nobody until a person's turn
  writes it again. A deployment server (no household) offers nothing for nobody's objects.
- A login handoff lives in memory; none survives a restart.
- Losing the agent's key volume: a new key on start, every stored delegation stops verifying, and
  `redelegate` shows each as it claims to be.

**Tests** (seam 5): `ScheduleDelegationTests`, `WatchDelegationTests`, `GlucoseWatchDelegationTests`,
`WebLoginHandoffTests`, `RedelegationTests`, `ChatMonitorDelegationTests`, `DelegationVerifierTests`.
`TestDelegations` signs with the suite's agent key, forges with the accounts one, and checks a fire as
the conversation group does.
