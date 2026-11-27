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

Who a turn or a call is for is signed, and **a claim nobody signed is not believed**. Two issuers: the **accounts service** signs *assertions* (who a turn is for), the **agent** signs *call tokens* (who one call acts for, bound to one server) and *delegations* (what a deferred turn may act as). `Domain/Security/Signing/` is the one piece both issuers sign with and every verifier verifies with.

## Why it is in Domain

Every consumer reaches Domain and nothing lower: `Mcp.Hosting` references Domain alone and **must never gain Infrastructure** (`Dockerfile.base-sdk` says why); Observability and the accounts service reach Domain alone too. Two Domain rules bend for it, on purpose and no further:

- **No HTTP.** `JwksCache` takes the fetch as a `Func<CancellationToken, Task<string>>`; each host hands in its own `HttpClient`.
- **State that outlives an operation.** `SigningKeyStore` and `JwksCache` are each one singleton per issuer, found by nothing — not registries.

## The token

Compact JWS, **ES256** (signature raw R||S) — the spec asks for Ed25519, but .NET's BCL has none. A header other than `alg: ES256` with a `kid` is malformed: `none` and HMAC never reach a key.

`IdentityClaims` is the one claim set: `iss`, `aud` (one string), `sub` (the account's user id, or `IdentityClaims.GuestSubject` = `guest`, never hex), `tid`, `chn`, `roles`, `iat`, `nbf`, `exp` (whole seconds), `jti`, and on the session check's assertion alone `sid`: the session the chat hub refreshes its connection's person by, never after it is revoked; no other token carries one, and the agent drops it with the envelope.

- Build with `IdentityClaims.Person(...)` / `.Guest(...)` / `.Nobody(chn)`, date with `.Stamped(issuer, audience, now, lifetime)` — also how the agent re-signs a verified identity per server.
- An assertion's audience is `TokenAudiences.Assertion` for every consumer; what binds it to one use is its `chn`, which the accounts service takes from the session check's `?channel=` or the resolution API's query (`.claude/rules/accounts.md`, `.claude/rules/channel-identity.md`).
- A delegation is a record derived from `IdentityClaims`; `TokenSigner.Sign<T>` and `TokenVerifier.VerifyAsync<T>` carry what it adds. `TokenIssuers` spells the two `iss` values.

**A refused token carries no claims.** The guest is a signed claim; a missing or failing token is nobody, **never the guest**.

**Nobody is signed too, as nobody.** Every call the agent makes carries a token, and calls acting for no one (channel plumbing, a fire nobody vouched for, a turn whose channel named no account) are signed with `IdentityClaims.Nobody(chn)`: `sub` = `IdentityClaims.NobodySubject` = `nobody`, the one subject that may leave `tid` out — the verifier still refuses an empty subject, and an empty household for anyone else. `Account` is null for nobody and the guest. **Only a call token says nobody**: the accounts service never signs one, and the agent's turn-start check refuses an assertion that does (`NamesNobody`).

## Verifying

`TokenVerifier` checks the signature before the payload, so a forgery is reported as a forgery; the `TokenRefusal` is what a gate reports, and `KeysUnavailable` means the issuer could not be reached with no copy — a network to look at, not a token to distrust. `JwksCache` refetches for an unknown key id (how a rotated key is picked up without a restart) at most once per `KeysRefetchInterval`, one fetch at a time, and a failed fetch keeps the copy there was. The fetch is bounded by `KeysFetchTimeout` inside the cache, not by the host's `HttpClient`, so an issuer that never answers is `KeysUnavailable` within the bound on every host alike. The agent verifies its own delegations against its `SigningKeyStore` directly (an `IVerificationKeys`, wired into `DelegationVerifier` in `InjectorModule`), not through a JWKS fetch.

## Keys

`SigningKeyStore.Open(SigningKeySettings)`: `PrivateKey` set (P-256 PKCS#8 DER, base64, one line) makes that the only key, nothing written; otherwise `KeyDirectory/signing-keys.json`, generated on first start. The last key signs; every earlier one is still published; the key id is its RFC 7638 thumbprint.

Rotation and retirement are commands run beside the issuer (`Rotate()`, `Retire()`); the running issuer notices the file change on its next signature. **Retire only after the old key's last token has run out**: the token lifetime plus the verifiers' `ClockSkew` (`AgentCommand.RetireAfter`; the accounts service's rotate command prints the same for assertions). Retire unpublishes every key but the current one.

## Tests

- `Tests/TestSigningKeys.cs` holds one fixed key per issuer for every test and eval stack — private keys in git, so `DeploymentConformanceTests.TheTestSigningKeys_AreNeverShipped` holds every shipped file (`DeploymentFiles.Shipped()`) to containing neither key nor its id.
- `TestAssertions` signs assertions with the accounts key (`Of`, `OfGuest`, `OfNobody`, `Forged`) and hands an in-process app `Verifier()`; a container accounts service with `SIGNING__PRIVATEKEY` = the test key verifies the same through its real JWKS.
- The suite's servers verify against `TestJwks`. `McpTestSecret.Transport` signs every tool call as the agent's clients do (`SigningClientTransport`), for whoever the `_meta` names — pass `audience:` for a server reached through a published port under another name; `UnsignedTransport` presents the credential alone. Standing in for the conversation group, `Transport` signs a context naming a person's account and no identity as that person (`Vouching`), one naming an identity as it, and one naming neither as nobody.
- A container the test process dials takes `WithCallTokensFromTheTestProcess()`; a stack with an agent container gives it `SIGNING__PRIVATEKEY` and points its servers at `http://agent:8080/internal/jwks`, as compose does; the eval's agent signs with the test key through `AgentSettings.Signing`.

## Call tokens

The agent's token for one call to one server.

- **Audience**: the name the server answers to, `ServerAddress.Name` — for a server dialled at a URL, its host, never the port — read by `TokenAudiences.CallTo(server)`, the same key `McpCredentials` finds its credential by. The server binds it as `MCP__AUDIENCE` (`CallTokenGate` takes it through `ServerAddress.Named`).
- **Where**: `params._meta["com.herfluffness/callToken"]` (`ChannelProtocol.CallTokenMetaKey`) on every `tools/call`, beside the conversation context.
- **Lifetime** 10 minutes, re-minted within `RefreshMargin` (5 min) of expiry (`CallTokens:Lifetime`, `CallTokens:RefreshMargin` in the agent's appsettings). Long because a check's page reads on its call's token for up to `Checks:Deadline` (user, 2026-11-27, accepting the longer replay window).
- **The agent signs where a message leaves.** `SigningClientTransport` wraps every MCP client the agent creates (`McpClientManager.Signed`: tool servers, outposts, each `McpChannelConnection`), so no call can be sent unsigned; it overwrites anything under the key. `CallTokens` keeps one token per identity and server until it nears expiry; its clock is the wall clock (`TimeProvider.System`), **not** the agent's registered `TimeProvider`, which an eval pins.
- **Who it names** is `CallIdentity.Of(turn)` over the `_meta` context: the `ConversationContext.Identity` the agent verified as the turn arrived (an assertion's person or guest-with-household, or a delegation's person). A context with none is nobody, **whatever principal it carries**. A worker carries its parent's context verbatim (`SubAgentRunTool`).
- **The server checks in `CallTokenGate`**, installed by `AddMcpHost` after the credential gate: an incoming message filter on **`tools/call` alone**, so the refusal is a JSON-RPC error (`McpProtocolException`, `InvalidRequest`, "Refused (<TokenRefusal>): …") the call-tool error filter never sees, and a server keeps one call-tool filter. Initialize, lists and resource reads pass on the credential alone. A refusal is published as an `ErrorEvent` (`CallTokenRefused`, `Service` the audience). Verified claims go on the message's `Items`, read by `CallTurn`; checked once on arrival, so a long command or a held approval runs to its end. The gate's clock is the wall clock unless a test registers one under `CallTokenGate.ClockKey`.
- **`CallTurn.Turn`** is the `_meta` context with its `Principal` and `Identity` replaced by the token's: what the agent wrote there is for its own signing, **never a server's belief**, and no tool reads the `_meta` around `CallTurn` (`CallTurnConformanceTests`). `IdentityClaims.Principal` is the one rule every reader of a verified identity uses: a person is `Principal(chn, sub, Account(tid, sub))`, the guest `Principal(chn, "guest")`, nobody none. **`Principal.Of` never reads the `_meta`'s `UserId` as anybody**, so a tool deciding who may do something (Telegram's `request_approval` among them) decides from the token's person or refuses. Only the household migration still reads that `UserId`, off pre-account history (`TopicOwners`).
- **The verified token goes back to the agent alone.** `call.VerifiedToken` (`CallTurn`) is the token as it came; the registrar hands it to a mount as `FileSystemCaller.CallToken`, and only the apps mount takes it (`CallHouseholdConformanceTests`): a check's page reads as the call's person on it, and the agent believes it as its own signature for the apps server it dials (`CheckCallVerifier`, `.claude/rules/apps.md`, "Checks"). Nothing new is minted, and a check ends within the token's refresh margin.
- **Every server checks**, the outpost included: a tunnelled server checks against the audience and keys its tunnel delivered (`TunnelTrust`). `IMcpHostSettings.ChecksCallTokens` is true for every server, and **no setting can switch it off**.
- **Keys and wiring.**
  - The agent's `SigningKeyStore` is `SIGNING__KEYDIRECTORY` = `/keys` (`./volumes/agent-keys`, chowned by `agent-volume`); its JWKS is `/internal/jwks` (`AgentKeysApi`), which no Caddy route reaches.
  - Each server binds `MCP__AUDIENCE` and `MCP__AGENTKEYSURL` (`http://agent:8080/internal/jwks`) in compose; **either left empty refuses every tool call**. A household's server on a tunnel binds neither: both arrive over its tunnel (`.claude/rules/tunnel.md`, "Keys").
  - The verifiers' timing is the shared policy file `Domain/token-verification.json` (`mcp:tokenVerification`), read by every host that verifies (the accounts service too, for its retirement wait) (`TokenVerificationPolicyFileTests`).
  - The agent dials its channels, and asks the accounts service anything on its own token, only once it is listening (`ChannelConnectionHost`, `ChannelAgentCatalogs` and `LemonadeModelRefresher` wait for `ApplicationStarted`), or a channel server's or the accounts service's first key fetch would fail and refuse calls — the accounts service every agent-signed proof, a shared server's call tokens too — until the refetch interval.
  - The agent's key is rotated and retired by its own commands (`delegations.md`, "The commands"), which also re-sign stored delegations — a delegation lives as long as its object.
