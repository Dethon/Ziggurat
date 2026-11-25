# 04: App sign-in

Spec: `.scratch/app-spaces/spec.md` ("App sign-in").

**What to build:** A person already signed in to Ziggurat opens a built app's link and lands in the app, signed in, with no second passkey prompt. A navigation on an app origin without an app session is redirected to a new route on the accounts service on the main host, which checks the person's ordinary session (passkey sign-in if there is none). It redirects back with a single-use code bound to that origin. The apps server redeems the code service-to-service for a person assertion on a new `apps` channel carrying `sid`, and sets a host-only `__Host-` cookie. App sessions are renewed by session refresh and end when the person's sessions end.

**Blocked by:** 03

**Status:** ready-for-agent

- [x] The accounts service issues codes only for origins under the configured apps domain, and refuses any other return target.
- [x] A code is single-use, short-lived and redeemable only for the origin it was issued to.
- [x] Redeeming answers an `apps`-channel assertion naming the person, the household and `sid`. The apps server believes nothing else about who the viewer is.
- [x] The apps server renews assertions by the session refresh, as the chat hub does, and never after the session is revoked.
- [x] `SessionsEnded` on the accounts channel ends every app session of that person.
- [x] The apps server alone decides whether the signed-in person may open the app. Anyone else gets a 404.
- [x] Accounts integration tests cover issue, single use, origin binding and redeem. An apps server test covers a session ending.

## Comments

**Landed (branch `app-spaces`).** The wire is spelled once in `Domain/Apps/AppSignIn`. Accounts side:
`GET /accounts/app-sign-in?returnUrl=&state=` (`Accounts/Apps/AppSignInRoute`), codes in memory
(`AppSignInCodes`), redemption `POST /accounts/internal/app-sign-in` (`Internal/AppSignInRedemption`,
caller `mcp-apps` on the new `AppChannels.Apps`). Apps side: `McpServerApps/Serving/AppViewers` (redirect,
callback `/_app/sign-in`, renewal) and `EndedAppSessionsListener`; the client is `IAppSignIns` on
`AccountsResolverClient`. Contracts in `.claude/rules/apps.md` ("App sign-in"), `accounts.md` ("The app
sign-in"), `channel-identity.md` and `person-assertions.md`.
Tests: `AppSignInRouteTests` (accounts: issue, refusals, signed-out trip, single use, origin binding,
expiry, ended session, caller, suspension, refresh channel), `AccountsResolverClientTests` (redeem and
renew over the wire), `AppsSignInTests` (apps server against a real accounts service: redirect, sign-in,
non-author 404, spent and wrong-origin codes, the browser's state, ending on `SessionsEnded` over Redis,
renewal with no announcement, renewal of a live session, outage), a Chromium case in
`AppsServingInABrowserTests`, `AppSignInTests` (pure rules), `SuspensionCoverageTests` entries and five
`DeploymentConformanceTests` cases.

**Choices the spec left open:**
- *The route carries the whole address*, `returnUrl` (as every accounts page does), not a bare origin:
  the origin is read from it and the path goes back as `path`, so a person lands where they were going.
  Accepted only when `AppOrigin.Parse` reads the host under `AppsDomain`, https (plain http only under a
  `.localhost` domain), no userinfo; an empty apps domain accepts nothing.
- *The apps domain is its own accounts setting* (`APPSDOMAIN`, the `x-apps-domain` anchor merged into
  `accounts`), not an `ALLOWEDORIGINS__*` entry: that list is also Fido2's origins and is held to the
  Caddyfile's hosts.
- *A browser-binding state* beyond the spec: the apps server sets a fresh state in `__Host-app-sign-in`
  and sends it through the accounts route, which carries it back; the callback checks it before
  redeeming. Without it, a person's own code sent to someone else in a link would sign that browser in
  as them (login CSRF). The accounts route refuses a request with no state of the right shape.
- *Lifetimes:* code 1 min (`AppSignInCodeLifetime`), app session 12 h (`Sessions:Lifetime`), assertion
  renewed after 5 min (`Sessions:AssertionKept`, at most half `AssertionLifetime`, conformance-checked),
  state cookie 10 min. A code is taken on the first redemption whatever origin it names.
- *The session refresh takes an optional `channel`*, which the caller must be allowed; the apps server
  names `apps`. Without it a host run's one shared credential would be answered on `signalr`.
- *Renewal outcomes:* nobody, a suspended household, or an assertion naming another person or `sid`
  ends that app session; an outage or an unverifiable answer serves 503 and ends nothing. Renewal runs
  on the next request after `AssertionKept`, so a missed announcement leaves a revoked session opening
  pages for at most 5 minutes.
- *`SessionsEnded` ends every app session of the person* (`EndAll`), unasked, as the ticket says; the hub's
  per-session ask is not ported.
- *No existence oracle:* the viewer is settled before the app is looked up. Any sessionless page on a
  well-formed label is sent to sign in; a removed app still answers its 410 first. Two cases of
  `AppsServingTests`' one-404 test became non-navigations for that reason.
- *Origin scheme:* the apps server names its origin from `X-Forwarded-Proto` (Caddy's) else the
  listener's scheme; the pages listener is reached only through the proxy.
- *A refused code* is a 400 page and never another redirect, so a broken sign-in cannot loop between hosts.
- *Caddy is unchanged:* `/accounts/app-sign-in` falls under the main host's unchecked `/accounts/*`, and
  the redemption under `/accounts/internal/*`, which every host answers 404 (conformance-checked).
- *Host runs:* `McpServerApps/appsettings.Local.json` names the local accounts service, its keys and
  Redis; `run-local.sh` and the VS Code launch give the accounts service `CALLERS__mcp-apps`; the
  accounts service's `appsettings.Local.json` sets `apps.localhost`. `.zed/debug.json` was not touched.

**Not verified:** the smoke tier and E2E were not run (out of scope); the `mcp-apps` and `accounts` images
were not built. Only the in-process servers and Chromium on `apps.localhost` exercised the flow.

**Deploying this needs:** rebuild `base-sdk` first (Domain changed), then `accounts` and `mcp-apps`
(`webui` and `caddy` are unchanged by this ticket). No new secret: `mcp-apps` reuses `MCP_SECRET_APPS`
and `REDIS_PASSWORD`, and now starts after `redis` is healthy. `APPSDOMAIN` must still be set to the real
apps domain (ticket 03's note); the accounts service now reads it too.
