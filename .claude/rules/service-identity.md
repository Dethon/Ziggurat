---
paths:
  - "Domain/Security/Signing/**"
  - "Tests/Unit/Domain/Security/Signing/**"
  - "Tests/TestSigningKeys.cs"
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
