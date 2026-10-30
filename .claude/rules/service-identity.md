---
paths:
  - "Domain/Security/Signing/**"
  - "Domain/Security/McpGateSettings.cs"
  - "Domain/token-verification.json"
  - "Mcp.Hosting/CallTokenGate.cs"
  - "Mcp.Hosting/CallTurn.cs"
  - "Infrastructure/Agents/Mcp/CallTokens.cs"
  - "Infrastructure/Agents/Mcp/CallIdentity.cs"
  - "Infrastructure/Agents/Mcp/SigningClientTransport.cs"
  - "Agent/App/AgentKeysApi.cs"
  - "Tests/Unit/Domain/Security/Signing/**"
  - "Tests/TestSigningKeys.cs"
  - "Tests/TestJwks.cs"
  - "Tests/McpTestSecret.cs"
  - "Tests/TestAssertions.cs"
  - "Domain/Security/AssertionVerifier.cs"
  - "Domain/Security/AppChannels.cs"
  - "Domain/Security/ForwardedAccount.cs"
  - "Infrastructure/Clients/Accounts/AssertionVerifierRegistration.cs"
  - "Observability/OperatorGate.cs"
  - "McpServerSugary/App/Person.cs"
  - "McpChannelSignalR/Hubs/SignedInConnectionFilter.cs"
  - "Agent/App/AgentApi.cs"
  - "Domain/Monitor/ConversationGroup.cs"
  - "Domain/Monitor/ChatMonitor.cs"
  - "Infrastructure/Clients/Accounts/AccountsResolverClient.cs"
  - "McpChannelSignalR/Hubs/ChatHub.cs"
  - "McpChannelTelegram/Services/TelegramAccess.cs"
  - "McpChannelVoice/Services/TranscriptDispatcher.cs"
  - "McpServerSugary/Channel/SugaryChannel.cs"
  - "Domain/Security/DelegationVerifier.cs"
  - "Domain/Security/DelegatedObject.cs"
  - "Domain/Security/DelegatedWrites.cs"
  - "Domain/Contracts/IDelegationStore.cs"
  - "Domain/DTOs/Channel/FireDelegation.cs"
  - "Domain/Tools/Scheduling/Vfs/ScheduleDelegation.cs"
  - "Domain/Tools/HomeAssistant/Vfs/WatchDelegation.cs"
  - "Domain/Tools/Web/LoginDelegation.cs"
  - "Mcp.Hosting/DelegationStoreTools.cs"
  - "Infrastructure/Agents/Mcp/McpFileSystemBackend.cs"
  - "Infrastructure/Agents/Mcp/Redelegation.cs"
  - "Agent/Commands/AgentCommand.cs"
  - "McpServerScheduling/Services/ScheduleDelegationStore.cs"
  - "McpServerHomeAssistant/Services/WatchDelegationStore.cs"
  - "Tests/TestDelegations.cs"
  - "Tests/Smoke/**"
---

# Service identity: signing and verifying

Who a turn or a call is for is signed, and a claim nobody signed is not believed
(`.scratch/service-identity/spec.md`). Two issuers: the **accounts service** signs *assertions* (who
a turn is for), the **agent** signs *call tokens* (who one call acts for, bound to one server) and
*delegations* (what a deferred turn may act as). `Domain/Security/Signing/` is the one piece both
issuers sign with and every verifier verifies with.

## Why it is in Domain

Every consumer reaches Domain and nothing lower: `Mcp.Hosting` references Domain alone and must
never gain Infrastructure (`Dockerfile.base-sdk` says why), Observability references Domain and its
client, and the accounts service references Domain alone, which base-sdk already builds. Infrastructure would leave three of the six consumers out, and a project of its own would
touch every host's Dockerfile.

Two Domain rules bend for it, on purpose and no further:

- **No HTTP.** `JwksCache` takes the fetch as a `Func<CancellationToken, Task<string>>`; each host
  hands in `ct => http.GetStringAsync(jwksUrl, ct)` over its own `HttpClient`.
- **State that outlives an operation.** `SigningKeyStore` keeps an issuer's keys in a file and
  `JwksCache` keeps a verifier's copy of an issuer's keys in memory. Both are found by nothing — a
  host holds one per issuer as a singleton — so they are not registries, but they are state.

## The token

Compact JWS, **ES256** (ECDSA P-256, SHA-256, signature as raw R||S). The spec asks for Ed25519
where the platform has it; .NET 10's BCL has none (no `Ed25519`/`EdDsa` type in
`System.Security.Cryptography`), so ES256 throughout. A header other than `alg: ES256` with a `kid`
is malformed — `none` and HMAC never reach a key.

`IdentityClaims` is the one claim set: `iss`, `aud` (one string, the one server or app it is for),
`sub` (the account's user id, or `IdentityClaims.GuestSubject` = `guest` — account ids are lowercase
hex, so it can never be one), `tid`, `chn` (channel id), `roles`, `iat`, `nbf`, `exp` (whole
seconds), `jti`. Build one with `IdentityClaims.Person(...)` or `.Guest(...)`, then date it with
`.Stamped(issuer, audience, now, lifetime)`, which is also how the agent re-signs a verified
identity per server. An assertion's audience is `TokenAudiences.Assertion`, one for every consumer
(the agent and the apps); what binds it to one use is its `chn`, which the accounts service takes
from the session check's `?channel=` or the resolution API's query (`.claude/rules/accounts.md`). A delegation is a record derived from `IdentityClaims`; `TokenSigner.Sign<T>`
and `TokenVerifier.VerifyAsync<T>` carry whatever a derived record adds. `TokenIssuers` spells the
two `iss` values.

**A refused token carries no claims.** The guest is a signed claim; a missing or failing token is
nobody, never the guest.

**Nobody is signed too, as nobody.** Every call the agent makes carries a token, and some act for no
one: its own channel plumbing (`channel_receive`, `send_reply`, `register_agents`…), a fire nobody
vouched for, a turn whose channel named no account. Those are signed with `IdentityClaims.Nobody(chn)`:
`sub` = `IdentityClaims.NobodySubject` = `nobody` (not hex either), the one subject that may leave
`tid` out — the verifier still refuses an empty subject, and an empty household for anyone else.
`Account` is null for nobody as for the guest. Only a call token says nobody: the accounts service
never signs one, and the agent's turn-start check refuses an assertion that does (`NamesNobody`).

## Verifying

`TokenVerifier(issuer, keys, clock, settings)` checks, in order: present, well formed, a key the
issuer publishes, the signature, then what the payload says — so a forgery is reported as a forgery,
not as whatever its claims would make it. The `TokenRefusal` is the reason a gate reports: `Missing`,
`Malformed`, `UnknownKey`, `KeysUnavailable` (the issuer could not be reached and there was no copy
— a network to look at, not a token to distrust), `BadSignature`, `WrongIssuer`, `WrongAudience`,
`Expired`, `NotYetValid`. 30 seconds of skew is forgiven on both dates.

`JwksCache` fetches when first asked, answers from its copy for `KeysLifetime` (10 min), fetches
again for a key id the copy lacks — how a rotated key is picked up without a restart — but never
twice within `KeysRefetchInterval` (10 s), one fetch at a time. A failed fetch keeps the copy there
was. The agent verifies its own delegations against its `SigningKeyStore` directly, which is also an
`IVerificationKeys`.

## Keys

`SigningKeyStore.Open(SigningKeySettings)`: with `PrivateKey` set (P-256 PKCS#8 DER, base64, one
line) that key is the only one and nothing is written; otherwise `KeyDirectory` holds
`signing-keys.json`, generated on first start, mode 0600, replaced whole by a rename. The last key in
it signs; every earlier one is still published. The key id is the key's RFC 7638 thumbprint.

Rotation and retirement are commands run beside the issuer (`Rotate()`, `Retire()` on a store opened
over the same directory); the running issuer notices the file change on its next signature. Retire
only after the old key's last token and every verifier's copy have run out: the longest token
lifetime plus `KeysLifetime`. Retire unpublishes every key but the current one.

## Apps behind the proxy

The chat hub, Sugary's API, the dashboard and the agent registration API believe the person
`X-Ziggurat-Assertion` names and **never** the plain `X-Ziggurat-User`/`-Tenant`/`-Roles` beside it,
which anything on the compose network could write. `AssertionVerifier` (`Domain/Security`) is the
one check: `TokenVerifier` for `TokenIssuers.Accounts` and `TokenAudiences.Assertion`, then the
assertion must name the app's channel and a person — a guest assertion is nobody to an app. It
answers a `ForwardedAccount` built from the signed claims, or null, logging a refusal other than
`Missing` at warning (`KeysUnavailable` there means the accounts service is unreachable and every
signed-in person is being turned away). `ForwardedAccount.From`, which read the plain three, is gone.

| App | Channel it holds the assertion to | Signed out | Else |
|---|---|---|---|
| Chat hub (`SignedInConnectionFilter`) | `ChannelIds.SignalR` | connection closed "not signed in" | verified once per connection, never re-asked as it ages |
| Sugary `/api` (`Person.SignedInAsync`, in `SugaryApp`'s gate) | `SugaryChannel.ChannelId` | 401 | verified once per request, the `Person` kept in `HttpContext.Items` for the routes' binders |
| Dashboard, everything served (`OperatorGate`) | `AppChannels.Dashboard` | 401 | 403 without the `operator` role |
| `/api/agents*` (`AgentApi`, after the secret) | `AppChannels.SexyTime` | 401 | the secret alone opens nothing |

The channel is what keeps an assertion signed for one route from opening another: each Caddy
`forward_auth` names its block's (`?channel=`), the session check signs it, the app checks it.

Each host binds `Accounts:JwksUrl` (`ACCOUNTS__JWKSURL` in compose for `mcp-channel-signalr`,
`mcp-sugary`, `observability`, `agent`, conformance-checked; `AssertionVerifier.DefaultJwksUrl` is
the same address, so a test stack wires nothing) and registers one verifier over a `JwksCache`:
`AddAssertionVerifier(url)` (`Infrastructure/Clients/Accounts`) for the three that reference
Infrastructure, `AddOperatorGate(url)` for Observability, which references Domain alone. A host run
reads the accounts service on `http://localhost:5004` (`appsettings.Local.json`).

## Tests

`Tests/TestSigningKeys.cs` holds one fixed key per issuer for every test and eval stack.
`Tests/TestAssertions.cs` signs assertions with the accounts one (`Of`, `OfGuest`, `Forged` — the
agent's key, so a foreign one) and hands an in-process app `Verifier()` over that key's store, no
accounts service needed; a container stack whose accounts service runs with
`SIGNING__PRIVATEKEY` = the test key verifies the same assertions through its real JWKS. They are
private keys in git; `DeploymentConformanceTests.TheTestSigningKeys_AreNeverShipped` holds every
shipped file (`DeploymentFiles.Shipped()`) to containing neither key nor its id.

The suite's servers verify the agent's tokens against `TestJwks`, one listener for the run serving
the test agent key's JWKS on every interface (containers reach it at `host.docker.internal`).
`McpTestSecret.Gate` answers to `localhost` and fetches from it; `McpTestSecret.Transport` signs every
tool call as the agent's clients do (the same `SigningClientTransport`, over `McpTestSecret.CallTokens`,
the test key on the wall clock), for whoever the call's `_meta` names — pass `audience:` for a
server reached through a published port under another name. `UnsignedTransport` presents the
credential alone, for a test that writes its own token or none. A container the test process dials
takes `WithCallTokensFromTheTestProcess()` (`MCP__AUDIENCE` = `TestContainers.DialledHost`, keys from
`TestJwks`); a stack with an agent container gives it `SIGNING__PRIVATEKEY` and points its servers at
`http://agent:8080/internal/jwks`, as compose does; the eval's agent signs with the test key through
`AgentSettings.Signing`.

## Call tokens

The agent's token for one call to one server. **Audience**: the host the agent dials the server at
(`TokenAudiences.CallTo(address)`, the same key `McpCredentials` finds its credential by, never the
port), which the server binds as `MCP__AUDIENCE`. **Where**: `params._meta["com.herfluffness/callToken"]`
(`ChannelProtocol.CallTokenMetaKey`) on every `tools/call`, beside the conversation context.
**Lifetime** 2 minutes, a fresh one minted once the last is within `RefreshMargin` (30 s) of expiry
(`CallTokens:Lifetime`, `CallTokens:RefreshMargin` in the agent's appsettings).

- **The agent signs where a message leaves.** `SigningClientTransport` wraps every MCP client the
  agent creates (`McpClientManager.Signed`: tool servers and outposts alike, and each
  `McpChannelConnection`), so a tool the model calls, an fs_* operation, a reply and an approval are
  signed by the same code and none can be sent unsigned; it overwrites anything under the key.
  `CallTokens` keeps one token per identity and server and reuses it until it nears expiry — a reply
  streamed in hundreds of `send_reply` calls costs one signature. Its clock is the wall clock
  (`TimeProvider.System`), not the agent's registered `TimeProvider`, which an eval pins.
- **Who it names** is `CallIdentity.Of(turn)` over the context in the same `_meta`: the
  `ConversationContext.Identity` the agent verified as the turn arrived — from a person's assertion
  (the person, or the household's guest *with its household*) or from a fire's delegation (the
  person) — for as long as the turn runs. A context with none is nobody, **whatever principal it
  carries**: a principal is only what a channel or a server stamped, and nothing but a verified
  assertion or a verified delegation names a person. A worker carries its parent's context verbatim
  (`SubAgentRunTool`), so it signs for its parent's person with no plumbing of its own.
- **The server checks in `CallTokenGate`**, which `AddMcpHost` installs after the credential gate: an
  incoming message filter on `tools/call` alone, so the refusal is a JSON-RPC error
  (`McpProtocolException`, `InvalidRequest`, "Refused (<TokenRefusal>): …") the call-tool error
  filter never sees, and a server keeps one call-tool filter. Initialize, the tool and prompt lists and
  resource reads pass on the credential alone: the agent makes them while it builds a session, for
  nobody, and nothing but a tool call can read who it acts for. A refusal is logged and published as
  an `ErrorEvent` (`ErrorType` `CallTokenRefused`, `Service` the audience) on a host that publishes
  metrics. The verified claims go on the message's `Items`, which `CallTurn` reads; checked once on
  arrival, so a long command or a held approval runs to its end. The gate's clock is the wall clock
  unless a test registers one under `CallTokenGate.ClockKey`.
- **`CallTurn.Turn`** is the `_meta` context with its `Principal` and its `Identity` replaced by the
  token's (the identity the agent wrote there is for its own signing, never a server's belief): a person
  is `Principal(chn, sub, Account(tid, sub))` — the channel's own name for them is not signed, so it
  is not kept — the guest `Principal(chn, "guest")`, nobody none. `call.Identity` is the verified
  claims themselves. `Principal.Of` still falls back to the context's `UserId` for a person's own
  turn with no principal, which reaches no account.
- **The outpost** answers `IMcpHostSettings.ChecksCallTokens` with false, explicitly: it receives the
  token and is not required to check it, and `CallTurn` then names nobody. Every other server says
  yes by saying nothing, and no setting can switch the check off.
- **Keys and wiring.** The agent opens its `SigningKeyStore` from `Signing` (`SIGNING__KEYDIRECTORY`
  = `/keys`, the `./volumes/agent-keys` volume `agent-volume` chowns, the agent running as PUID) and
  serves the JWKS at `/internal/jwks` (`AgentKeysApi`), which no Caddy route reaches. Each server binds
  `MCP__AUDIENCE` and `MCP__AGENTKEYSURL` (`http://agent:8080/internal/jwks`) in compose; either left
  empty refuses every tool call. The verifiers' timing is `Domain/token-verification.json`
  (`mcp:tokenVerification`), read by `BindSettings` and the agent's configuration. The agent dials
  its channels only once it is listening (`ChannelConnectionHost` waits for `ApplicationStarted`), or a
  channel server's first fetch of its keys would fail and refuse calls until the refetch interval.
  The agent's key is rotated and retired by its own commands (Deferred turns, below), which also
  sign stored delegations again — a delegation lives as long as its object, not two minutes.

## Channels and the agent

No turn acts for a person the accounts service did not vouch for. A channel carries the service's
assertion on the turn's notification (`ChannelMessageNotification.Assertion`, mapped onto
`ChannelMessage.Assertion` by `McpChannelConnection`) and the agent believes that, never the
notification's `Principal`.

| Channel | Where its assertion comes from |
|---|---|
| Chat hub | a fresh one **per turn** from the resolution API (`signalr`, the connection's account): the connection's own was checked as it arrived and lives ten minutes, the connection hours; one the service cannot give ends the stream with an error and sends nothing |
| Telegram | the resolution's answer that admitted the sender, or a redemption's for somebody just linked, kept by `TelegramAccess` for `LinkCache:Linked` — at most half an assertion's life (conformance-checked) |
| Voice | the recognised speaker's resolution, else `POST /accounts/internal/guest` (`IAccountResolver.GuestAsync`); an outage leaves none, and the agent refuses and reports the turn |
| Sugary | the request's own `X-Ziggurat-Assertion` (`Person.Assertion`), the estimate's and each answer's |

**The check** is `AssertionVerifier.VerifyTurnAsync(assertion, channel)` — the agent's
`AddAssertionVerifier` singleton, so the copy of the accounts service's keys the registration API
already uses: `TokenIssuers.Accounts`'s signature, `TokenAudiences.Assertion`, in date with 30 s
of skew, `chn` the id of the connection the message came on (`WrongChannel`), and a person or the
guest — an assertion naming nobody is refused (`NamesNobody`), though `TokenVerifier` alone would
pass it. It answers a `TurnAdmission`: `Admitted(identity)` with the claims out of their envelope
(`IdentityClaims.Person`/`.Guest`, no dates, no id), or `Refused(reason)`, a `TokenRefusal`'s name
or one of those two.

**Where**: `ConversationGroup`'s message pump, as each message arrives and before it is queued —
not when its turn comes up, because a turn queued behind a long one (the chat client sends while a
reply streams) would otherwise be refused for the minutes it waited. Once admitted, the message's
principal keeps the channel's own `UserId` (the Telegram approval router's key) and takes the
`Account` from the identity (none for the guest), so the agent factory, memory recall and the review
copy's owner all read the verified account; the identity rides `Turn` into the turn's
`ConversationContext.Identity`, which is where `CallIdentity` reads it. No ambient state: a worker
inherits it by carrying its parent's context. The verifier's clock is the registered
`TimeProvider` (the wall clock in the agent host), and the check is never repeated, so a turn acts
for its person after its assertion has run out.

**Refusal**: logged at warning, published as an `ErrorEvent` (`ErrorType`
`ChatMonitor.AssertionRefusedErrorType` = `AssertionRefused`, `Service` `agent`, the message naming
the channel, the conversation and the reason), and answered on the channel as a failed turn ends —
an `Error` reply (`ConversationGroup.RefusedTurnAnswer`) then `StreamComplete`, under the message's
`TurnKey` or a minted one — so a browser, a satellite or a waiting app request is not left waiting.
Nothing is built, minted or recalled for it.

**Not checked**: a chat command (`/clear`, `/cancel`, a channel's cancel) is no turn and acts for
nobody. A message with `Origin` set — a schedule's fire, a login wake, a watch, a download — carries
no assertion and is never refused for lacking one: it acts for whoever its delegation verifies as, or
nobody (Deferred turns, below).

**Tests and the eval.** `FakeChannelConnection.WriteMessage` sends a person's message as a channel
server would, with an assertion signed by the accounts test key for its channel (the principal's
account, or the household guest's); `WriteUnsigned` sends one without. `TestAssertions.Verifier()`
is the verifier every monitor fixture is built with. The eval drives `McpAgent` below the monitor,
so `EvalRun.Turn` stamps the identity the check answers for the eval account's assertion on the
turn's channel, and on a schedule's, a watch's or a login's fire the one its delegation would verify
as (the eval account on the fire's channel); a download's fire none (`EvalRunTests` holds each to
the agent's own checks). A test's MCP client stands in for the agent: `McpTestSecret.Transport`
signs a context that names a person's account and no identity as that person (`Vouching`, the
conversation group's part), one that names an identity as it, and one naming neither as nobody.

## Deferred turns: delegations

A schedule's fire, a login wake and a watch's fire act for a person only because that person's turn
created or last rewrote the object, and only on the agent's own word for it: a **delegation**,
signed by the agent's key while that turn's call wrote the object. A server stores one and hands it
back; it cannot make one, move one onto another object, or fire a changed object as its writer.

**The token** is `Delegation` (`Domain/Security/Signing`), an `IdentityClaims` with three claims of
its own: `knd` (`schedule`, `watch`, `login` — `DelegatedObject`'s constants), `oid` (the schedule's
id, the watch's id, the asking conversation's id) and `dig`, base64url SHA-256 of the object's
**description**: canonical JSON of what it does, computed on both ends by one Domain function per
kind (`ScheduleDelegation`, `WatchDelegation`, `LoginDelegation`):

| Kind | Description (`DelegatedObject.cs`) | What the fire must match |
|---|---|---|
| schedule | agent, prompt, cron, runAt (unix seconds, UTC), deliverTo as written | `Origin.ScheduleId` = oid, `AgentId`, `Content` = prompt, `ReplyTo` = the deliverTo resolved against the delivery default |
| watch | the prompt effects' prompts as written, deliverTo as the write resolved it | `Origin.WatchId` = oid, `ReplyTo` as above; the prompt arrives rendered by the home and is not compared |
| login | the asking turn's agent, conversation and origin | `AgentId`; then either the asking conversation answering at its origin (Done) or one fresh chat-client conversation (`[signalr, null]`, a voice login's link) |

`iss` the agent, `aud` `TokenAudiences.Delegation`, `sub`/`tid`/`chn`/`roles` the person's (only a
person is delegated — the guest and nobody delegate nothing), `exp` **iat + 100 years** for a
schedule or a watch (`Delegation.ObjectLifetime`: as long as the object; its deletion, a rewrite or
retiring the key ends it) and **iat + `Delegations:HandoffLifetime`** (1 hour; the handoff's tab
closes within its 10 + 30 minutes) for a login.

**Minted where the write leaves.** `SigningClientTransport` signs one for a call acting for a person,
under `_meta["com.herfluffness/delegation"]` (`ChannelProtocol.DelegationMetaKey`), beside the call
token, for exactly what the call writes: a filesystem write states its object under the agent-only
`ChannelProtocol.DelegateMetaKey` (`McpFileSystemBackend`, through `DelegatedWrites`: a schedules or
`ha` create from its content, an edit by reading the file first and laying the edits over it as the
mount does, a schedule move by reading its file under the destination's name, a watch replace by
reading the watch whose delivery it keeps), and `web_login` is a login handoff by its name,
described off the call's own context. The transport removes both keys from every call and writes
the delegation only for a person, so neither a model nor a caller can put one there. An edit or a
move rests on what the server answered the agent's read with — a lying server gets a delegation for
its own text with the turn's edit on top — while a create gives it nothing to lie about. A runAt
with no offset is read in the agent's zone (`TimeZoneInfo.Local`), which compose's shared `TZ` makes
the scheduling server's; zones that disagree make such a one-shot fire for nobody.

**Stored with the object, replaced by every write.** The server reads it with `call.Delegation`
(`CallTurn`), never verifying it: `ScheduleFileSystem` keeps it on `Schedule.Delegation` for every
create, edit and move (a write carrying none clears it), `HaFileSystem` in the automation's metadata
(`HaWatchMetadata.Delegation`, through `FileSystemCaller.Delegation`), `LoginHandoffs` on the
`LiveHandoff`. Deleting the object deletes it. `Schedule.CreatedBy` is still recorded — who last
wrote it, what redelegate offers for a schedule with none — and is never what a fire acts as.

**Fired with what the object does now.** `FireDelegation(Token, Object)` rides
`ChannelMessageNotification.Delegation`: the scheduler's from the stored schedule
(`ScheduleFirePlanner`), the watch callback's looked up from the home at fire time
(`WatchFiredEndpoint`, `HaWatches.GetAsync`; a home it cannot ask fires for nobody), both login
wakes' from the handoff. A download's carries none.

**Checked as the fire arrives**, in `ConversationGroup.AdmitAsync`, by `DelegationVerifier` (the
agent's own `SigningKeyStore` — every key it still publishes — on the wall clock, and
`Domain/delivery.json`, which the agent now reads too): the agent's signature, `aud`, a person, `knd`
the fire's origin kind, `dig` the presented description's digest, and the fire doing what the
description says. It answers `FireAdmission.Delegated(identity)` — the message's principal becomes
`Principal(chn, sub, account)` and the identity rides the turn as an assertion's does — or
`Undelegated(refusal)`: the principal is dropped and the turn runs as nobody. A refusal (a token that
does not verify, `NamesNoPerson`, `WrongKind`, `DigestMismatch`, `NotThisFire`, `NotDelegable` for a
download carrying one) is logged at warning and published as `DelegationRefused`; a fire carrying
none is ordinary and says nothing. A monitor built with no verifier believes no delegation. Fire
timing is not checked: nothing in a fire says when it was due, so the digest's timing only catches a
store edited under a server that still tells the truth about it. `run_now` fires a schedule as its
delegation says, whoever asked for it.

**The commands** (`Agent/Commands/AgentCommand.cs`, System.CommandLine, detected in `Program` before
anything builds and run instead of the agent): `rotate-signing-key` (a new key signs from the next
call on; the agent and every server pick it up by themselves), `redelegate [--yes]` and
`retire-signing-keys [--force]`. Both of the last dial every `ChannelEndpoint` as the agent does and
ask the servers that keep delegations through `delegations_list` / `delegations_replace`
(`DelegationStoreTools`, `AddDelegationStore<T>`, over an `IDelegationStore`: the scheduling and
Home Assistant servers; behind the call-token gate, signed as nobody, and stripped from every model's
tool list by `ThreadSession`). `Redelegation` leaves the current key's delegations alone, re-signs
without asking one an earlier published key signed for the object as it is now, and shows the
operator — and signs a fresh one over what the object does now only on a yes — one it cannot believe
(a key lost, an object changed since) and a schedule with none whose recorded writer names an
account. A replace naming another object is refused by the server. Retirement refuses while a
stored delegation is still an earlier key's, or a server cannot be asked; the order is rotate, then
redelegate, then retire (after the call-token lifetime plus skew). A provided key
(`SIGNING__PRIVATEKEY`) is neither rotated nor retired and every verb exits 1.

**Deploying over existing objects.** Schedules written before this have no delegation and fire for
nobody until the operator runs `redelegate` once, which offers each as the writer its `CreatedBy`
names (the household migration stamped an account on every one); a declined or unoffered one stays
nobody's until a person's turn rewrites it. Watches fired for nobody before this, and still do until
a person's turn writes them again. A login handoff lives in memory and none survives the restart.
Losing the agent's key volume is the same story: the agent generates a new key on start, every
stored delegation stops verifying, and `redelegate` shows each as it claims to be.

**Tests** (seam 5): `ScheduleDelegationTests` (the real scheduling server, written through the
agent's `McpFileSystemBackend` and signing transport), `WatchDelegationTests` (the real Home
Assistant server over the eval's fake home, fired through its callback and read off an agent channel
connection), `WebLoginHandoffTests` (Done and the voice link wake; a forged token and another
conversation refused), `RedelegationTests` (lost key, no writer, rotation and the retirement guard)
and `ChatMonitorDelegationTests` (the monitor's admission, a download, the error feed).
`TestDelegations` signs with the suite's agent key, forges with the accounts one, and checks a fire
as the conversation group does.

## The smoke tier (seam 6)

`StackSmokeTests` asks a running stack (`scripts/smoke-stack.sh`) what the signing promises, from
outside through Caddy and from **inside** the compose network (`SmokeStack.FromInsideAsync`, curl in
the `camoufox` container, which sits beside every server). Every gated server it runs (compose's
`MCP__AUDIENCE`) refuses a `tools/call` naming a person with its real credential and no token
(`Missing`), a garbage one (`Malformed`) and one signed with the suite's agent key (`UnknownKey` —
not `KeysUnavailable`, which is how a server that cannot reach the agent's JWKS shows). The apps
refuse plain identity headers and a foreign-signed assertion when reached past Caddy; an assertion
the stack's own session check signed opens its own app and not another's channel; both JWKS answer
inside and on no host. The reply test waits for an assistant message that is **not**
`.chat-message.error`: a refused turn is answered too, with text in it. Its stack's keys are generated
fresh on each `up`; nothing in it holds the test keys.
