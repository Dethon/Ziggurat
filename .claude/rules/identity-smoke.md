---
paths:
  - "Tests/Smoke/**"
---

# Service identity: the smoke tier

See also: `service-identity.md` (tokens, refusals, call tokens), `person-assertions.md` (apps and turns), `delegations.md` (fires).

## The smoke tier (seam 6)

`StackSmokeTests` asks a running stack (`scripts/smoke-stack.sh`) what the signing promises, from
outside through Caddy and from **inside** the compose network (`SmokeStack.FromInsideAsync`, curl in
the smoke override's `smoke-probe` container, which sits beside every server and app).

- Every gated server it runs (compose's `MCP__AUDIENCE`) refuses a `tools/call` naming a person with
  its real credential and no token (`Missing`), a garbage one (`Malformed`) and one signed with the
  suite's agent key (`UnknownKey` — not `KeysUnavailable`, which is how a server that cannot reach the
  agent's JWKS shows).
- The apps refuse plain identity headers and a foreign-signed assertion when reached past Caddy; an
  assertion the stack's own session check signed opens its own app and not another's channel; both
  JWKS answer inside and on no host.
- The reply test waits for an assistant message that is **not** `.chat-message.error`: a refused turn
  is answered too, with text in it.
- Its stack's keys are generated fresh on each `up`; nothing in it holds the test keys.
