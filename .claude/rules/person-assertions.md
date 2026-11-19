---
paths:
  - "Domain/Security/AssertionVerifier.cs"
  - "Domain/Security/AppChannels.cs"
  - "Domain/Security/ForwardedAccount.cs"
  - "Domain/Contracts/ISessionAccounts.cs"
  - "Domain/Monitor/ConversationGroup.cs"
  - "Domain/Monitor/ChatMonitor.cs"
  - "Domain/Monitor/ApprovalRelay.cs"
  - "Domain/Contracts/IAccountStanding.cs"
  - "Infrastructure/Agents/HouseholdAccountStanding.cs"
  - "Infrastructure/Clients/Accounts/AssertionVerifierRegistration.cs"
  - "Infrastructure/Clients/Accounts/AccountsResolverClient.cs"
  - "Observability/OperatorGate.cs"
  - "Agent/App/AgentApi.cs"
  - "Agent/App/TopicMintingApi.cs"
  - "McpChannelSignalR/Hubs/SignedInConnectionFilter.cs"
  - "McpChannelSignalR/Hubs/ChatHub.cs"
  - "McpChannelTelegram/Services/TelegramAccess.cs"
  - "McpChannelVoice/Services/TranscriptDispatcher.cs"
  - "McpServerSugary/App/Person.cs"
  - "McpServerSugary/Channel/SugaryChannel.cs"
  - "Tests/TestAssertions.cs"
---

# Service identity: person assertions

See also: `service-identity.md` (the token, verifying, keys, call tokens), `delegations.md` (fires acting for a person), `identity-smoke.md` (the smoke tier).

The accounts service signs an *assertion* naming who a request or a turn is for; apps and the agent
believe that and nothing a channel or a header says beside it.

## Apps behind the proxy

The chat hub, Sugary's API, the dashboard and the agent registration API believe the person
`X-Ziggurat-Assertion` names and **never** the plain `X-Ziggurat-User`/`-Tenant`/`-Roles` beside it,
which anything on the compose network could write.

- `AssertionVerifier` (`Domain/Security`) is the one check: `TokenVerifier` for
  `TokenIssuers.Accounts` and `TokenAudiences.Assertion`, then the assertion must name the app's
  channel and a person — a guest assertion is nobody to an app. It answers a `ForwardedAccount` built
  from the signed claims, or null, logging a refusal other than `Missing` at warning (`KeysUnavailable`
  there means the accounts service is unreachable and every signed-in person is being turned away).
- Each app asks **`AdmitAsync(request.Headers, channel, may, ct)`**, the one decision all five share
  (`AppAdmission`: `Admitted(person, assertion)`, `SignedOut` (401), `Forbidden(person)` (403) for a
  person `may` — the role the app asks for — turns away), and says only how it answers each: the
  hub's `HubException`, Sugary's `Person` (which keeps the assertion to hand on), the dashboard's
  status, the agent API's 401 and its typed owner (`AgentApiOwner`), the admin API's refusal body. It
  reads the one header through `AssertionVerifier.AssertionOf` — none, or two values, is no
  assertion. `AppAdmissionTests` pins the decision once.

| App | Channel it holds the assertion to | Signed out | Else |
|---|---|---|---|
| Chat hub (`SignedInConnectionFilter`) | `ChannelIds.SignalR` | connection closed "not signed in" | verified once per connection, never re-asked as it ages |
| Sugary `/api` (`Person.SignedInAsync`, in `SugaryApp`'s gate) | `SugaryChannel.ChannelId` | 401 | verified once per request, the `Person` kept in `HttpContext.Items` for the routes' binders |
| Dashboard, everything served (`OperatorGate`) | `AppChannels.Dashboard` | 401 | 403 without the `operator` role |
| Dashboard's usage route, `/api/usage` (`OperatorGate`) | `AppChannels.Usage`, signed by the accounts service itself for the owner its admin gate verified (`Assertions.Of(ForwardedAccount, channel)`), never by a proxy block | 401 | 403 without the `owner` role; answers the assertion's household alone |
| `/api/agents*` (`AgentApi`, after the secret) | `AppChannels.SexyTime` | 401 | the secret alone opens nothing |
| The accounts service's admin API, `/accounts/admin/*` (`AdminApi`'s gate) | `AppChannels.Admin` | 401 | verified against the service's own keys; 403 without the root's role or the admin client's header (`.claude/rules/admin.md`) |

The channel is what keeps an assertion signed for one route from opening another: each Caddy
`forward_auth` names its block's (`?channel=`), the session check signs it, the app checks it.

Each host binds `Accounts:JwksUrl` (`ACCOUNTS__JWKSURL` in compose for `mcp-channel-signalr`,
`mcp-sugary`, `observability`, `agent`, conformance-checked; `AssertionVerifier.DefaultJwksUrl` is
the same address, so a test stack wires nothing) and registers one verifier over a `JwksCache`:
`AddAssertionVerifier(url)` (`Infrastructure/Clients/Accounts`) for the three that reference
Infrastructure, `AddOperatorGate(url)` for Observability, which references Domain alone. A host run
reads the accounts service on `http://localhost:5004` (`appsettings.Local.json`).

## Channels and the agent

No turn acts for a person the accounts service did not vouch for. A channel carries the service's
assertion on the turn's notification (`ChannelMessageNotification.Assertion`, mapped onto
`ChannelMessage.Assertion` by `McpChannelConnection`) and the agent believes that, never the
notification's `Principal`.

| Channel | Where its assertion comes from |
|---|---|
| Chat hub | a fresh one **per turn** for the connection's **session** (`/accounts/internal/session`, by the `sid` the session check signed into the connection's assertion; `ISessionAccounts`): the connection's own was checked as it arrived and lives ten minutes, the connection hours. A session revoked since ends the stream "signed out" and sends nothing — the spec's "stops the next one" — and an outage ends it with an error too. No caller can ask for a `signalr` person by account id |
| Telegram | the resolution's answer that admitted the sender, or a redemption's for somebody just linked, kept by `TelegramAccess` for `LinkCache:Linked` — at most half an assertion's life (conformance-checked) |
| Voice | the recognised speaker's resolution, else `POST /accounts/internal/guest` (`IAccountResolver.GuestAsync`); an outage leaves none, and the agent refuses and reports the turn |
| Sugary | the request's own `X-Ziggurat-Assertion` (`Person.Assertion`), the estimate's and each answer's |

**The check** is `AssertionVerifier.VerifyTurnAsync(assertion, channel)` on the agent's
`AddAssertionVerifier` singleton (the registration API's copy of the accounts service's keys):
`TokenIssuers.Accounts`'s signature, `TokenAudiences.Assertion`, in date with 30 s of skew, `chn` the
id of the connection the message came on (`WrongChannel`), and a person or the guest — an assertion
naming nobody is refused (`NamesNobody`), though `TokenVerifier` alone would pass it. It answers a
`TurnAdmission`: `Admitted(identity)` with the claims out of their envelope
(`IdentityClaims.Person`/`.Guest`, no dates, no id), or `Refused(reason)`, a `TokenRefusal`'s name or
one of those two.

**Where**: `ConversationGroup`'s message pump, as each message arrives and before it is queued — not
when its turn comes up, or a turn queued behind a long one (the chat client sends while a reply
streams) would be refused for the minutes it waited. Once admitted, the message's principal keeps the
channel's own `UserId` (the Telegram approval router's key) and takes the `Account` from the identity
(none for the guest), so the agent factory, memory recall and the review copy's owner all read the
verified account; the identity rides `Turn` into the turn's `ConversationContext.Identity`, where
`CallIdentity` reads it. No ambient state: a worker inherits it by carrying its parent's context. The
verifier's clock is the registered `TimeProvider`, and the check is never repeated, so a turn acts for
its person after its assertion has run out.

**Refusal**: logged at warning, published as an `ErrorEvent` (`ErrorType`
`ChatMonitor.AssertionRefusedErrorType` = `AssertionRefused`, `Service` `agent`, the message naming
the channel, the conversation and the reason), and answered on the channel as a failed turn ends — an
`Error` reply (`ConversationGroup.RefusedTurnAnswer`) then `StreamComplete`, under the message's
`TurnKey` or a minted one — so a browser, a satellite or a waiting app request is not left waiting.
Nothing is built, minted or recalled for it.

**Disabled members.** An assertion says who a turn is for as of when it was signed, and a channel may
hold one for minutes (Telegram's `LinkCache:Linked`), so once it verifies the group also asks whether
the household still lets the agent act for that person: `IAccountStanding` (`HouseholdAccountStanding`,
the turn's household's agent view, `AgentHouseholdConfig.DisabledPeople`, kept a minute and dropped
when accounts announces `HouseholdConfigProtocol.PeopleChanged`).

- A member an owner disabled is refused before anything is built: logged, an `ErrorEvent`
  `AccountDisabled` (`ChatMonitor.AccountDisabledErrorType`) under their household, and
  `ConversationGroup.DisabledAnswer` then `StreamComplete` on the channel.
- Before that the household itself is asked whether it runs (`.claude/rules/channels.md`, "A
  suspended household starts nothing").
- A standing or a suspension that cannot be read refuses the turn as `StandingUnreadable`
  (`ChatMonitor.StandingUnreadableErrorType`) with `ConversationGroup.UncheckedAnswer` — "could not
  be checked right now", never "suspended" or "disabled", and never let through.
- One `StandingRefusal` (`Suspended`, `Disabled`, `Unchecked`, `SugaryOff`) answers every check and
  one `RefuseByStandingAsync` reports it, for a turn and a fire alike.
- **An approval is held to the same standing** (`ApprovalRelay`, given the group's
  `IAccountStanding`; user's decision, 2026-11-05): a question waiting on a member disabled meanwhile
  is refused everywhere it shows, asked again every `ApprovalRelay.StandingCheck` (15 s; one that
  cannot be read refuses nothing), and an answer that lets something through is believed only while
  its asker is still active — Telegram holds who a sender is for minutes; an asker who cannot be
  checked is refused. A refusal needs nobody's standing.
- The guest and nobody are never asked about their standing (the guest's household is asked whether
  it runs). A monitor built with no `IAccountStanding` or no `IAgentDefinitionProvider` (a test
  harness) asks nobody that question; the agent's host registers both in `AddAgent`.

**Not checked**: a chat command (`/clear`, `/cancel`, a channel's cancel) is no turn and acts for
nobody. A message with `Origin` set — a schedule's fire, a login wake, a watch, a download — carries
no assertion and is never refused for lacking one: it acts for whoever its delegation verifies as, or
nobody (`delegations.md`).

**Tests and the eval.**

- `FakeChannelConnection.WriteMessage` sends a person's message as a channel server would, with an
  assertion signed by the accounts test key for its channel (the principal's account, or the
  household guest's); `WriteUnsigned` sends one without. `TestAssertions.Verifier()` is the verifier
  every monitor fixture is built with.
- The eval drives `McpAgent` below the monitor, so `EvalRun.Turn` stamps the identity the check
  answers for the eval account's assertion on the turn's channel, and on a schedule's, a watch's or a
  login's fire the one its delegation would verify as (the eval account on the fire's channel); a
  download's fire none (`EvalRunTests` holds each to the agent's own checks).
