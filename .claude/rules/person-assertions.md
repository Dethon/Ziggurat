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
  `TokenIssuers.Accounts` and `TokenAudiences.Assertion`, then the app's channel and a person — a guest
  assertion is nobody to an app. Refusals other than `Missing` log at warning (`KeysUnavailable`: every
  signed-in person is being turned away).
- Each app asks **`AdmitAsync(request.Headers, channel, may, ct)`**, the one decision all five share
  (`AppAdmission`: `Admitted(person, assertion)`, `SignedOut` 401, `Forbidden(person)` 403 for a role
  `may` turns away), and says only how it answers each. The header is read through
  `AssertionVerifier.AssertionOf` — none, or two values, is no assertion. `AppAdmissionTests` pins it.

| App | Channel it holds the assertion to | Signed out | Else |
|---|---|---|---|
| Chat hub (`SignedInConnectionFilter`) | `ChannelIds.SignalR` | connection closed "not signed in" | verified once per connection, never re-asked as it ages |
| Sugary `/api` (`Person.SignedInAsync`, in `SugaryApp`'s gate) | `SugaryChannel.ChannelId` | 401 | verified once per request, the `Person` kept in `HttpContext.Items` |
| Dashboard, everything served (`OperatorGate`) | `AppChannels.Dashboard` | 401 | 403 without the `operator` role |
| Dashboard's usage route, `/api/usage` (`OperatorGate`) | `AppChannels.Usage`, signed by the accounts service itself for the owner its admin gate verified (`Assertions.Of(ForwardedAccount, channel)`), never by a proxy block | 401 | 403 without the `owner` role; answers the assertion's household alone |
| `/api/agents*` (`AgentApi`, after the secret) | `AppChannels.SexyTime` | 401 | the secret alone opens nothing |
| The accounts service's admin API, `/accounts/admin/*` (`AdminApi`'s gate) | `AppChannels.Admin` | 401 | verified against the service's own keys; 403 without the root's role or the admin client's header (`.claude/rules/admin.md`) |
| The agent's app read route (`AppReader`, after the apps server's own credential) | `AppChannels.Apps`, the app session's assertion the apps server forwards — or, for a check, no assertion but the agent's own call token (`CheckCallVerifier`, `apps.md` "Checks") | 401 | a person only; then the household's standing, as a turn's (`HouseholdStanding`); `.claude/rules/apps.md`, "The app read API" |
| The apps server's published-apps listing (`PublishedAppsListing`) | `AppChannels.PublishedApps`, signed by the accounts service itself for the owner its admin gate verified (`HouseholdAppsClient`), never by a proxy block | 401 | 403 without the `owner` role; answers the assertion's household alone |
| The apps server's listing of a person's apps (`PersonsAppsListing`) | `AppChannels.PersonsApps`, the session refresh's answer to the chat hub alone, for the connection's session (`PersonsAppsClient`), never by a proxy block | 401 | any person; answers that person's own published apps and the links of the household's apps named as shared |
| A built app's origin (`AppViewers`, apps server) | `AppChannels.Apps`, from the app sign-in's redemption, never a header (no proxy block) | a page is sent to the app sign-in, anything else the 404 | verified once per sign-in and renewal, kept on the app session; whether they may open the app is the apps server's (`.claude/rules/apps.md`) |

The channel is what keeps an assertion signed for one route from opening another: each Caddy
`forward_auth` names its block's (`?channel=`), the session check signs it, the app checks it.

Each host binds `Accounts:JwksUrl` (`ACCOUNTS__JWKSURL` in compose, conformance-checked;
`AssertionVerifier.DefaultJwksUrl` is the same address) and registers one verifier over a `JwksCache`:
`AddAssertionVerifier(url)` (`Infrastructure/Clients/Accounts`), or `AddOperatorGate(url)` for
Observability, which references Domain alone.

## Channels and the agent

No turn acts for a person the accounts service did not vouch for, but for a check's ask (below). A channel carries the service's
assertion on the turn's notification (`ChannelMessageNotification.Assertion`, mapped onto
`ChannelMessage.Assertion` by `McpChannelConnection`) and the agent believes that, never the
notification's `Principal`.

| Channel | Where its assertion comes from |
|---|---|
| Chat hub | a fresh one **per turn** for the connection's **session** (`/accounts/internal/session`, by the `sid` signed into the connection's assertion; `ISessionAccounts`), since the connection outlives its own ten-minute assertion. A session revoked since ends the stream "signed out" and sends nothing; an outage ends it with an error. No caller can ask for a `signalr` person by account id |
| Telegram | the resolution's answer that admitted the sender, or a redemption's for somebody just linked, kept by `TelegramAccess` for `LinkCache:Linked` — at most half an assertion's life (conformance-checked) |
| Voice | the recognised speaker's resolution, else `POST /accounts/internal/guest` (`IAccountResolver.GuestAsync`); an outage leaves none, and the agent refuses and reports the turn |
| Sugary | the request's own `X-Ziggurat-Assertion` (`Person.Assertion`), the estimate's and each answer's |
| Apps (a built app's ask) | the app session's `apps` assertion the apps server keeps; a person only, never the guest (`TurnAdmission.NotAPerson`) |

**The one exception: a check's ask.** A built app's ask during `./check` carries no assertion but
`ChannelMessageNotification.CheckCallToken`: the agent's own call token for the tool call that ran the
check, handed back by the apps server. It is believed on an app turn from the deployment's `apps`
channel endpoint alone (`AppTurns.Believed` drops it anywhere else) and only as `CheckCallVerifier`
believes a check's reads — the agent's own key, in date, audience `TokenAudiences.CallTo(sharedServers.apps)`,
naming a person; the guest, nobody or another server's token is refused, and **both together are
refused** (`TurnAdmission.BothAnAssertionAndACheck`). The person it names acts on the `apps` channel
(the identity's channel restamped), then standing, the `apps` feature and the building agent apply as
to any ask. Why: nobody is signed in to a check's preview, and the token is the agent's own word that
this person's turn asked for it, good for the check's deadline (`ChatMonitor`'s optional
`CheckCallVerifier`; none refuses every check's ask).

**The check** is `AssertionVerifier.VerifyTurnAsync(assertion, channel)` on the agent's
`AddAssertionVerifier` singleton: the accounts signature, `TokenAudiences.Assertion`, in date with 30 s of skew, `chn`
the id of the connection the message came on (`WrongChannel`), and a person or the guest — an
assertion naming nobody is refused (`NamesNobody`), though `TokenVerifier` alone would pass it. It
answers a `TurnAdmission`: `Admitted(identity)`, or `Refused(reason)` — a `TokenRefusal`'s name or one of those two. The group adds two for a built app's ask (`NotAPerson`, `NotAnAppTurn`; `.claude/rules/channels.md`). Its clock is the registered `TimeProvider`.

**Where**: `ConversationGroup`'s message pump, as each message arrives and before it is queued — never
when its turn comes up, or a turn queued behind a long one would be refused for the minutes it
waited — and never again, so a turn acts for its person after its assertion has run out. Once
admitted, the principal keeps the channel's own `UserId` (the Telegram approval router's key) and
takes the `Account` from the identity (none for the guest); the identity rides `Turn` into
`ConversationContext.Identity`, where `CallIdentity` reads it. No ambient state: a worker inherits it
by carrying its parent's context.

**Refusal**: logged at warning, an `ErrorEvent` `ChatMonitor.AssertionRefusedErrorType`
(`AssertionRefused`, `Service` `agent`, naming channel, conversation and reason), and answered on the
channel as a failed turn ends — `ConversationGroup.RefusedTurnAnswer` then `StreamComplete`, under the
message's `TurnKey` or a minted one — so nothing is left waiting. Nothing is built, minted or recalled
for it.

**Disabled members.** An assertion says who a turn is for as of when it was signed, and a channel may
hold one for minutes, so once it verifies the group also asks whether the household still lets the
agent act for that person: `IAccountStanding` (`HouseholdAccountStanding`, over
`AgentHouseholdConfig.DisabledPeople`, kept a minute and dropped on
`HouseholdConfigProtocol.PeopleChanged`).

- A disabled member is refused before anything is built: logged, `ErrorEvent` `AccountDisabled`
  (`ChatMonitor.AccountDisabledErrorType`) under their household, `ConversationGroup.DisabledAnswer`
  then `StreamComplete`.
- Before that the household is asked whether it runs (`.claude/rules/channels.md`, "A suspended
  household starts nothing").
- A standing or suspension that cannot be read refuses the turn as `StandingUnreadable`
  (`ChatMonitor.StandingUnreadableErrorType`) with `ConversationGroup.UncheckedAnswer` — never
  "suspended" or "disabled", and never let through.
- One `StandingRefusal` (`Suspended`, `Disabled`, `Unchecked`, `SugaryOff`, `AppsOff`, `AppAgentGone`) answers every check and one
  `RefuseByStandingAsync` reports it, for a turn and a fire alike.
- **An approval is held to the same standing** (`ApprovalRelay`, given the group's `IAccountStanding`;
  user's decision, 2026-11-05): a question waiting on a member disabled meanwhile is refused everywhere
  it shows, rechecked every `ApprovalRelay.StandingCheck` (one that cannot be read refuses nothing), and
  an answer that lets something through is believed only while its asker is still active; an asker who
  cannot be checked is refused. A refusal needs nobody's standing.
- The guest and nobody are never asked about their standing (the guest's household is asked whether it
  runs). A monitor built without `IAccountStanding` or `IAgentDefinitionProvider` (a test harness) asks
  nobody; the agent's host registers both in `AddAgent`.

**Not checked**: a chat command (`/clear`, `/cancel`, a channel's cancel) is no turn and acts for
nobody. A message with `Origin` set (a fire, a login wake, a watch, a download) carries no assertion and
is never refused for lacking one: it acts for whoever its delegation verifies as, or nobody
(`delegations.md`).

**Tests and the eval.** `FakeChannelConnection.WriteMessage` sends a message with an assertion signed by
the accounts test key for its channel (`WriteUnsigned` without); `TestAssertions.Verifier()` builds every
monitor fixture. The eval drives `McpAgent` below the monitor, so `EvalRun.Turn` stamps the identity the
check would answer (and a fire's delegation would verify as; a download's fire none), held to the
agent's own checks by `EvalRunTests`.
