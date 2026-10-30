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
never signs one, and the agent's turn-start check (ticket 05) must refuse an assertion that does.

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
- **Who it names** is `CallIdentity.Of(turn)` over the context in the same `_meta`: today the
  account on the turn's principal is the person and anything else is nobody — the voice guest
  included, because nothing in the agent knows a guest's household. **Ticket 05 changes that one
  method** to answer the identity the agent verified from the turn's assertion, carried on the
  turn's `ConversationContext`; nothing else in the signing path moves. A worker carries its parent's
  context verbatim (`SubAgentRunTool`), so it signs for its parent's person with no plumbing of its own.
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
- **`CallTurn.Turn`** is the `_meta` context with its `Principal` replaced by the token's: a person
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
  There is no rotation command for the agent's key yet.
