# 0027 — Static endpoints fail, dynamic ones are dropped

Status: accepted
Date: 2026-08-15

## Context

`McpClientManager.CreateClientsWithRetry` dials every endpoint of an agent in one
`Task.WhenAll`, under a Polly retry of three attempts and a ten second initialization
timeout, with no per-endpoint catch. One endpoint that never answers therefore fails
`ThreadSession.BuildAsync`, and the agent gets no session at all.

That is the right behaviour for the endpoints the repo has had until now. All of them are
containers in one compose stack: they come up with the agent, they are on the same network,
and one being unreachable is a fault worth surfacing rather than degrading around. An agent
that silently ran without the vault would be worse than an agent that refused to run.

Outposts break the assumption. An outpost is a filesystem on someone's machine, reached at an
address that machine registered, and its registration outlives it by up to the keepalive TTL.
A laptop that shuts its lid is reachable-then-not inside its own window, which is normal and
nobody's fault. Under the existing rule it would take the whole session down: Jonas would lose
the vault, the sandbox and home automation because a machine went to sleep.

## Decision

Endpoints are dialled under two rules, decided by where the endpoint came from.

A **configured** endpoint keeps today's behaviour. It is named in `appsettings.json`, it is
part of the deployment, and a failure to dial it fails the session.

A **dynamic** endpoint — one contributed by an outpost registration — is best effort. A
failure to dial it is logged and the endpoint is dropped, the session is built from the rest,
and that outpost's mount is simply not there for this session. Nothing retries it inside the
session; the next session build asks the registry again.

The distinction is the endpoint's origin rather than its address or its behaviour, because
origin is the only thing that says whether absence is a fault. A container in the compose file
being down is a bug. A laptop being asleep is Tuesday.

## Considered options

**Make every endpoint best effort.** One rule, nothing two-tier to explain, and no risk of
classifying an endpoint wrongly. Rejected: it converts every real outage into a quiet
degradation. An agent that lost the vault would answer from nothing and log a line, which is
not how anyone finds out that a container is down.

**Keep the strict rule and shorten the TTL.** Ten second keepalive, twenty-five second
expiry, so a stale registration is rarely dialled. Rejected as a narrowing rather than a fix:
it leaves the same failure with a smaller window, it triples the keepalive traffic, and it
does nothing at all for a machine that is up but wedged, which fails the dial just the same.

**Probe dynamic endpoints before building the session.** Ask each registered outpost whether
it is alive, then dial only the ones that answered. Rejected: the probe is the dial. Anything
short of a real MCP handshake can succeed against a host that will then fail to handshake, and
a real handshake done twice pays the cost twice on the path where latency is user-visible.

## Consequences

- An outpost that is misconfigured — wrong advertised address, wrong port, firewall in the way —
  looks exactly like an outpost that is asleep. The registration succeeds, the mount never
  appears, and the only evidence is a log line in the agent. The keepalive response carrying the
  mount verdict back to the machine exists because of this.
- The two rules live in one code path, and the temptation to unify them will recur. This record
  is the answer: they are not two implementations of one idea, they are one implementation of two
  different meanings of "not reachable".
- A conversation's mounts can differ from one session to the next without anything having
  changed in configuration. That is already true of nothing else in the repo, and it is the price
  of mounts that belong to machines rather than to the deployment.
