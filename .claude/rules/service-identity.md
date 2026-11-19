---
paths:
  - "Domain/Security/Signing/**"
  - "Domain/Security/McpGateSettings.cs"
  - "Domain/Security/ServerAddress.cs"
  - "Domain/token-verification.json"
  - "Mcp.Hosting/CallTokenGate.cs"
  - "Mcp.Hosting/CallTurn.cs"
  - "Infrastructure/Agents/Mcp/CallTokens.cs"
  - "Infrastructure/Agents/Mcp/CallIdentity.cs"
  - "Infrastructure/Agents/Mcp/SigningClientTransport.cs"
  - "Agent/App/AgentKeysApi.cs"
  - "Agent/Commands/AgentCommand.cs"
  - "Tests/Unit/Domain/Security/Signing/**"
  - "Tests/TestSigningKeys.cs"
  - "Tests/TestJwks.cs"
  - "Tests/McpTestSecret.cs"
  - "Tests/TestAssertions.cs"
---

# Service identity: signing and verifying

See also: `person-assertions.md` (apps and turns believing the accounts service's assertions), `delegations.md` (deferred turns acting for a person), `identity-smoke.md` (the smoke tier).

Who a turn or a call is for is signed, and a claim nobody signed is not believed
(`.scratch/service-identity/spec.md`). Two issuers: the **accounts service** signs *assertions* (who
a turn is for), the **agent** signs *call tokens* (who one call acts for, bound to one server) and
*delegations* (what a deferred turn may act as). `Domain/Security/Signing/` is the one piece both
issuers sign with and every verifier verifies with.

## Why it is in Domain

Every consumer reaches Domain and nothing lower: `Mcp.Hosting` references Domain alone and must
never gain Infrastructure (`Dockerfile.base-sdk` says why), Observability references Domain and its
client, and the accounts service Domain alone. Infrastructure would leave three of the six consumers
out; a project of its own would touch every host's Dockerfile.

Two Domain rules bend for it, on purpose and no further:

- **No HTTP.** `JwksCache` takes the fetch as a `Func<CancellationToken, Task<string>>`; each host
  hands in `ct => http.GetStringAsync(jwksUrl, ct)` over its own `HttpClient`.
- **State that outlives an operation.** `SigningKeyStore` (an issuer's keys, in a file) and
  `JwksCache` (a verifier's copy of an issuer's keys, in memory) are each held as one singleton per
  issuer and found by nothing — not registries, but state.

## The token

Compact JWS, **ES256** (ECDSA P-256, SHA-256, signature as raw R||S): the spec asks for Ed25519, but
.NET's BCL has none, 11 included. A header other than `alg: ES256` with a `kid` is malformed — `none`
and HMAC never reach a key.

`IdentityClaims` is the one claim set: `iss`, `aud` (one string, the one server or app it is for),
`sub` (the account's user id, or `IdentityClaims.GuestSubject` = `guest` — account ids are lowercase
hex, so it can never be one), `tid`, `chn` (channel id), `roles`, `iat`, `nbf`, `exp` (whole
seconds), `jti`, and on the session check's assertion alone `sid`, the session it checked (what the
chat hub refreshes its connection's person by; the agent drops it with the envelope).

- Build one with `IdentityClaims.Person(...)` or `.Guest(...)`, date it with
  `.Stamped(issuer, audience, now, lifetime)` — also how the agent re-signs a verified identity per
  server.
- An assertion's audience is `TokenAudiences.Assertion` for every consumer (the agent and the apps);
  what binds it to one use is its `chn`, which the accounts service takes from the session check's
  `?channel=` or the resolution API's query (`.claude/rules/accounts.md`, `.claude/rules/channel-identity.md`).
- A delegation is a record derived from `IdentityClaims`; `TokenSigner.Sign<T>` and
  `TokenVerifier.VerifyAsync<T>` carry whatever a derived record adds. `TokenIssuers` spells the two
  `iss` values.

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
issuer publishes, the signature, then the payload — so a forgery is reported as a forgery, not as
whatever its claims would make it. The `TokenRefusal` is the reason a gate reports; `KeysUnavailable`
means the issuer could not be reached and there was no copy — a network to look at, not a token to
distrust. 30 seconds of skew is forgiven on both dates.

`JwksCache` answers from its copy for `KeysLifetime` and refetches for an unknown key id (how a
rotated key is picked up without a restart), at most once per `KeysRefetchInterval`, one fetch at a
time. The fetch is bounded by `KeysFetchTimeout` inside the cache, not by the host's `HttpClient`, so
an issuer that never answers is `KeysUnavailable` within the bound on every host alike. A failed fetch
keeps the copy there was. The
agent verifies its own delegations against its `SigningKeyStore` directly, which is also an
`IVerificationKeys`.

## Keys

`SigningKeyStore.Open(SigningKeySettings)`: with `PrivateKey` set (P-256 PKCS#8 DER, base64, one
line) that key is the only one and nothing is written; otherwise `KeyDirectory` holds
`signing-keys.json`, generated on first start, mode 0600, replaced whole by a rename. The last key
signs; every earlier one is still published. The key id is the key's RFC 7638 thumbprint.

Rotation and retirement are commands run beside the issuer (`Rotate()`, `Retire()` on a store opened
over the same directory); the running issuer notices the file change on its next signature. Retire
only after the old key's last token has run out: the token lifetime plus the verifiers' `ClockSkew`
(`AgentCommand.RetireAfter`; the accounts service's rotate command prints the same for assertions). Retire unpublishes every key but the current one.

## Tests

- `Tests/TestSigningKeys.cs` holds one fixed key per issuer for every test and eval stack. They are
  private keys in git; `DeploymentConformanceTests.TheTestSigningKeys_AreNeverShipped` holds every
  shipped file (`DeploymentFiles.Shipped()`) to containing neither key nor its id.
- `Tests/TestAssertions.cs` signs assertions with the accounts key (`Of`, `OfGuest`, `OfNobody`,
  `Forged` — signed with the agent's key, so a foreign one) and hands an in-process app `Verifier()`
  over that key's store, no accounts service needed. A container stack whose accounts service runs
  with `SIGNING__PRIVATEKEY` = the test key verifies the same assertions through its real JWKS.
- The suite's servers verify the agent's tokens against `TestJwks`, one listener for the run serving
  the test agent key's JWKS on every interface (containers reach it at `host.docker.internal`).
- `McpTestSecret.Gate` answers to `localhost` and fetches from it. `McpTestSecret.Transport` signs
  every tool call as the agent's clients do (the same `SigningClientTransport`, over
  `McpTestSecret.CallTokens`, the test key on the wall clock), for whoever the call's `_meta` names —
  pass `audience:` for a server reached through a published port under another name.
  `UnsignedTransport` presents the credential alone, for a test that writes its own token or none.
  Standing in for the agent, `Transport` signs a context that names a person's account and no
  identity as that person (`Vouching`, the conversation group's part), one that names an identity as
  it, and one naming neither as nobody.
- A container the test process dials takes `WithCallTokensFromTheTestProcess()` (`MCP__AUDIENCE` =
  `TestContainers.DialledHost`, keys from `TestJwks`); a stack with an agent container gives it
  `SIGNING__PRIVATEKEY` and points its servers at `http://agent:8080/internal/jwks`, as compose does;
  the eval's agent signs with the test key through `AgentSettings.Signing`.

## Call tokens

The agent's token for one call to one server.

- **Audience**: the name the server answers to, `ServerAddress.Name` (`Domain/Security/ServerAddress.cs`)
  — for a server dialled at a URL, its host, never the port — read by `TokenAudiences.CallTo(server)`,
  the same key `McpCredentials` finds its credential by. The server binds it as `MCP__AUDIENCE`
  (`CallTokenGate` takes it through `ServerAddress.Named`).
- **Where**: `params._meta["com.herfluffness/callToken"]` (`ChannelProtocol.CallTokenMetaKey`) on
  every `tools/call`, beside the conversation context.
- **Lifetime** 2 minutes, a fresh one minted once the last is within `RefreshMargin` (30 s) of expiry
  (`CallTokens:Lifetime`, `CallTokens:RefreshMargin` in the agent's appsettings).
- **The agent signs where a message leaves.** `SigningClientTransport` wraps every MCP client the
  agent creates (`McpClientManager.Signed`: tool servers, outposts and each `McpChannelConnection`),
  so a model's tool call, an fs_* operation, a reply and an approval are signed by the same code and
  none can be sent unsigned; it overwrites anything under the key. `CallTokens` keeps one token per
  identity and server until it nears expiry. Its clock is the wall clock (`TimeProvider.System`), not the agent's registered
  `TimeProvider`, which an eval pins.
- **Who it names** is `CallIdentity.Of(turn)` over the context in the same `_meta`: the
  `ConversationContext.Identity` the agent verified as the turn arrived — from a person's assertion
  (the person, or the household's guest *with its household*) or from a fire's delegation (the
  person) — for as long as the turn runs. A context with none is nobody, **whatever principal it
  carries**: a principal is only what a channel or a server stamped, and only a verified assertion or
  a verified delegation names a person. A worker carries its parent's context verbatim
  (`SubAgentRunTool`), so it signs for its parent's person with no plumbing of its own.
- **The server checks in `CallTokenGate`**, which `AddMcpHost` installs after the credential gate: an
  incoming message filter on `tools/call` alone, so the refusal is a JSON-RPC error
  (`McpProtocolException`, `InvalidRequest`, "Refused (<TokenRefusal>): …") the call-tool error
  filter never sees, and a server keeps one call-tool filter. Initialize, the tool and prompt lists
  and resource reads pass on the credential alone: the agent makes them while building a session, for
  nobody, and only a tool call can read who it acts for. A refusal is logged and published as an
  `ErrorEvent` (`ErrorType` `CallTokenRefused`, `Service` the audience) on a host that publishes
  metrics. The verified claims go on the message's `Items`, which `CallTurn` reads; checked once on
  arrival, so a long command or a held approval runs to its end. The gate's clock is the wall clock
  unless a test registers one under `CallTokenGate.ClockKey`.
- **`CallTurn.Turn`** is the `_meta` context with its `Principal` and its `Identity` replaced by the
  token's (the identity the agent wrote there is for its own signing, never a server's belief).
  `IdentityClaims.Principal` is the one rule every reader of a verified identity uses (the call, a
  fire's delegated person, a minted topic's owner): a person is `Principal(chn, sub, Account(tid, sub))`
  — the channel's own name for them is not signed, so it is not kept — the guest
  `Principal(chn, "guest")`, nobody none. `call.Identity` is the verified claims themselves.
  `Principal.Of` is the context's `Principal` and nothing else: the `UserId` the `_meta` carries is
  never read as anybody, so a call signed for nobody names nobody — no channel, no user — and a tool
  deciding who may do something (Telegram's `request_approval` among them) decides from the token's
  person or refuses. Only the household migration still reads that `UserId`, off persisted
  pre-account history, as evidence of who started a topic (`TopicOwners`).
- **Every server checks**, the outpost included: a household's server on a tunnel checks against the
  audience and keys its tunnel delivered (`TunnelTrust`). `IMcpHostSettings.ChecksCallTokens` is true
  for every server, and no setting can switch it off.
- **Keys and wiring.**
  - The agent opens its `SigningKeyStore` from `Signing` (`SIGNING__KEYDIRECTORY` = `/keys`, the
    `./volumes/agent-keys` volume `agent-volume` chowns, the agent running as PUID) and serves the
    JWKS at `/internal/jwks` (`AgentKeysApi`), which no Caddy route reaches.
  - Each server binds `MCP__AUDIENCE` and `MCP__AGENTKEYSURL` (`http://agent:8080/internal/jwks`) in
    compose; either left empty refuses every tool call. A household's server on a tunnel binds
    neither: its audience and the agent's keys arrive over its tunnel (`TunnelTrust`), the keys again
    before the first call signed with a rotated one, so a rotation reaches every household without a
    public route (`.claude/rules/tunnel.md`, "Keys").
  - The verifiers' timing is the shared policy file `Domain/token-verification.json`
    (`mcp:tokenVerification`), read by every host that verifies (the accounts service too, for the
    skew its retirement waits out) and handed to every verifier it builds and to the commands' waits
    (`TokenVerificationPolicyFileTests`).
  - The agent dials its channels only once it is listening (`ChannelConnectionHost` waits for
    `ApplicationStarted`), or a channel server's first fetch of its keys would fail and refuse calls
    until the refetch interval.
  - The agent's key is rotated and retired by its own commands (`delegations.md`, "The commands"),
    which also sign stored delegations again — a delegation lives as long as its object, not two
    minutes.
