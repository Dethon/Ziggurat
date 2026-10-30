# Service identity

Status: ready-for-agent

## Problem Statement

Every part of Ziggurat that acts for a person believes whoever tells it who that person is. An MCP
server reads the turn's principal from the call's `_meta` and acts on it, so anything inside the
deployment that holds that server's credential can claim to be any person and read their memory,
their remembered logins, their Sugary log or their schedules. The agent believes the principal a
channel server puts on its notification, so a channel server that misbehaves can start a turn as
anyone. The apps behind the proxy believe three plain headers, which only network isolation keeps
honest. A schedule's fire, a login wake and a watch's fire act for whoever the server that stored
them says wrote them.

The sandbox, which runs what the model writes, has been fenced off on a network of its own. Every
other container is still one credential leak away from being able to speak as anyone.

## Solution

Identity is signed, and a claim nobody signed is not believed.

The accounts service, the one owner of people, signs a short-lived assertion of who a turn is for
whenever a channel learns the person: the session check signs one for the apps and the chat client,
and the internal resolution API signs one for a Telegram link or a voice profile. Channel servers
carry that assertion on the turn they start. The agent verifies it against the accounts service's
public keys before the turn acts for anyone, and from then on signs its own short-lived token for
every call it makes to an MCP server, bound to that one server. Servers trust only the agent's key,
and refuse a call whose token is missing, expired, aimed at another server or not signed by it. A
household guest is a signed claim of its own, never the absence of one.

A turn nobody sent at that moment acts for the person on a delegation the agent signed when that
person's turn created the thing: a schedule, a login handoff, a watch. At fire time the agent
verifies its own signature and that the delegation names that very object, so a server can store a
delegation but never mint one.

The apps behind the proxy get the same signed assertion from the session check and stop believing
the plain headers alone. The browse server's public handoff pages move to a listener of their own.

## User Stories

### People

1. As a person, I want nothing inside the deployment to be able to act as me without the accounts service having vouched for me, so that one leaked credential does not expose my memory, logins or health data.
2. As a person, I want my scheduled task to act for me only because I wrote it, so that a misbehaving server cannot run a schedule as me.
3. As a person, I want a schedule I delete or rewrite to stop acting for me on its old terms, so that what fires is what I last asked for.
4. As a person who started a login handoff, I want only that handoff to wake my turn as me, so that a forged wake cannot use my logins.
5. As a person, I want a Home Assistant watch I set to fire as me only because I set it.
6. As a person in a long conversation, I want a turn that runs for hours to keep working, so that signing never cuts a long task short.
7. As a person, I want a session I revoke to stop being able to start turns within minutes.
8. As a household guest speaking to a satellite, I want the house to keep working for me, so that being nobody's account is not the same as being refused.

### The operator

9. As the operator, I want each MCP server to refuse a call that carries no valid token from the agent, so that holding a server's credential is no longer enough to act through it.
10. As the operator, I want a refused call reported on the errors feed, so that a signing mistake is visible rather than silent.
11. As the operator, I want the apps behind the proxy to verify who a request is for, so that a container on the internal network cannot forge a person to the chat hub, Sugary, the dashboard or SexyTime.
12. As the operator, I want signing keys generated and kept by the services that sign, with nothing new to put in `.env`, so that deploying this adds no secret to manage.
13. As the operator, I want to rotate a signing key by adding a new one and retiring the old after the caches have turned over, so that rotation never breaks running turns.
14. As the operator, I want the browse server's public pages on a listener of their own, so that no routing mistake can expose its `/mcp`.
15. As the operator, I want the smoke stack to prove the whole chain is signed end to end, so that a key or wiring mistake is caught before a deploy.
16. As SexyTime's server, I want to forward the signed assertion I received, so that registering a custom agent is tied to a person the accounts service vouched for.

### Developers

17. As a developer, I want the test and eval stacks to run with a fixed test signing key, so that the suites need no key provisioning and stay deterministic.
18. As a developer, I want a tool call made by a worker to carry its parent turn's identity, so that delegated work acts for the same person.
19. As a developer, I want the signed identity to be an explicit field the call carries, never ambient state, so that what a call acts for is visible where it is handled.

## Implementation Decisions

### Issuers and verifiers

- Two issuers. The **accounts service** signs *assertions* — who a turn is for, issued where a
  person is resolved. The **agent** signs *call tokens* — who this call acts for, issued per MCP
  server it calls — and *delegations* for deferred turns.
- Verifiers: the agent verifies assertions (accounts service's keys) and delegations (its own keys).
  MCP servers and channel servers verify call tokens (the agent's keys). Apps behind the proxy verify
  assertions (accounts service's keys).
- Token format: compact JWS with an asymmetric algorithm (Ed25519, or ES256 where the platform
  lacks it) and a key id. Claims: issuer, audience, subject (the account user id, or the guest
  marker), tenant id, channel id, roles where relevant, issued-at, expiry, and a unique id.
- A **guest** is a signed claim: an assertion or call token whose subject is the household guest of
  a tenant. A missing or invalid token is never read as the guest.

### Keys

- Each issuer generates its private key on first start and keeps it in its own volume, never in
  `.env`. Each publishes its current and previous public keys as a JWKS document on the internal
  network only (the proxy never routes it). Verifiers fetch the JWKS, cache it, and refetch on an
  unknown key id, rate-limited.
- Rotation: a command on each issuer adds a new key and makes it the signing key; the previous one
  stays published until every cached JWKS and every token signed with it has expired, and a later
  command retires it.

### Lifetimes and long turns

- An assertion lives 10 minutes. It only has to cover the hop from the channel to the agent: the
  agent verifies it once, when the turn starts, and holds the verified identity for the whole turn.
- A call token lives 2 minutes and names the one server it is for. The agent keeps one token per
  server per turn and mints a fresh one before it is within a margin of expiry, so a turn of any
  length never presents an expired token and never pays a signature per streamed chunk.
- A server checks a call's token when the call arrives, not again during it: a long command in the
  sandbox, a browse session or an approval waiting for its answer runs to its end.
- Anything that acts for the person after the turn, outside a call — a download finishing hours
  later, a fire — goes through a delegation, never a turn's token.
- A session revoked while a turn runs does not stop that turn; it stops the next one, which needs a
  fresh assertion. Checking revocation on every call was rejected as a round trip per call.
- Clock skew of 30 seconds is tolerated on every expiry and not-before check.

### Channels and the agent

- The session check signs an assertion for every session it admits and answers it in a fourth
  header, `X-Ziggurat-Assertion`, which the proxy copies like the other three. The internal
  resolution API answers an assertion with every identity it resolves.
- A channel server carries the assertion on the turn's notification. The agent refuses a turn whose
  assertion is missing, expired, not the accounts service's, or names a different channel than the
  one the notification came on, and reports it on the errors feed.
- Every call the agent makes to an MCP server or channel server carries its call token in `_meta`,
  agent-to-channel calls (`send_reply`, `request_approval` and the rest) included. A worker's calls
  carry the parent turn's verified identity, signed by the same agent. An outpost receives the token
  and is not required to check it.

### Servers

- The hosting library's gate gains a second check after the credential: a call's token must verify
  against the agent's keys, be unexpired, and name this server as its audience. A call failing it is
  refused with an error that says why (missing, expired, wrong audience, bad signature) and reported.
- What a server acts for comes from the verified token only. The unverified `_meta` principal is
  ignored by servers from here on.

### Deferred turns

- When a turn creates or rewrites a schedule, starts a login handoff or sets a watch, the agent signs
  a delegation naming the account, the tenant, the kind and the id of that object, and a digest of
  what it does (for a schedule: its prompt and timing), and the server stores it with the object.
- At fire time the server sends the stored delegation with the fire; the agent verifies its own
  signature, that the delegation names that object, and that the digest still matches. A schedule
  rewritten by someone else carries the rewriter's delegation; one rewritten by no verified turn has
  none and fires as nobody. A deleted object's delegation is gone with it.
- A download's fire stays nobody's.

### Apps behind the proxy

- The chat hub, Sugary, the dashboard and the agent registration API verify `X-Ziggurat-Assertion`
  against the accounts service's keys and take the person from it, no longer from the plain headers
  alone. A request whose assertion is missing or invalid is treated as signed out.
- SexyTime's server forwards the assertion it received to `/api/agents`; the API verifies it beside
  the agent-API secret. The change in SexyTime's own repository is noted, not made here.

### The browse server

- The public handoff pages and the logins page move to a second listener, which the proxy routes
  `/browse/*` to; `/mcp` stays on the internal listener alone.

## Testing Decisions

A good test checks what a caller observes: whether a call is admitted or refused and why, who a
call acts for, what the proxy hands an app. It never checks how a token is laid out beyond what a
verifier reads.

Six seams, two of them new:

1. **Token issue and verify (new).** Signing and verifying on their own: expiry and skew, audience,
   key id lookup, a tampered payload or signature, a key not in the JWKS, the guest marker.
2. **The accounts service's HTTP surface (existing).** The session check's fourth header and the
   resolution API's assertion verify against the service's JWKS; the JWKS is served on the internal
   network only. Prior art: `SessionCheckTests`, the internal-API tests.
3. **A real MCP server host (existing).** Unsigned, expired, wrong-audience and foreign-key calls are
   refused with their reasons; a person's and a guest's tokens are admitted and act as signed. Prior
   art: `McpSecretGateTests`.
4. **Channel servers and the agent (existing).** Notifications carry the assertion; the agent refuses
   a turn whose assertion fails and acts for the one that verifies; a long turn's calls keep valid
   tokens past the first token's expiry under a fake clock. Prior art: `TelegramBotServiceTests`,
   `TranscriptDispatcherTests`, the hub pipeline tests, `ChatMonitor` tests.
5. **Deferred turns (new).** A schedule's delegation is stored at write, verified at fire, refused
   after an edit by another turn or a tampered digest, and gone after a delete; a login wake and a
   watch fire likewise. Prior art: `ScheduleFileSystemJourneyTests`, `WebLoginHandoffTests`.
6. **The smoke tier (existing).** On a real stack, a signed-in reply, Sugary and the dashboard work
   with assertions verified, and a call to a server with a forged `_meta` principal and no token is
   refused.

Test and eval stacks use a fixed signing key pair per stack, generated by the fixture.

## Out of Scope

- More than one household and tenant configuration: the Tenants phase.
- Mutual TLS between containers.
- Checking session revocation on every call.
- The change in SexyTime's repository.
- Signing for outposts beyond carrying the token.

## Further Notes

- This is phase 3 of the multi-tenant plan (`.scratch/multi-tenant/plan.md`). It branches from
  `web-identity` and merges back into it; master gets every phase together.
- Deploying needs no new secret. Volumes for the agent's and the accounts service's keys survive
  rebuilds; losing one means re-issuing: sessions sign in again, schedules and watches are rewritten
  or re-delegated by a command the spec's tickets provide.
