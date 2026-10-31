# 09: Shared servers reach a household's server

**What to build:** A shared deployment server reaches a household's own server through the relay's internal endpoint, never the network: timers announce, dismiss and resolve satellites on the household's voice server. Spec: `../spec.md` ("The tunnel").

**Blocked by:** 03

**Status:** done

- [x] A timer for the home household rings, is dismissed and validates its satellite through the relay
- [x] A shared server cannot reach a household's server outside the relay's endpoint
- [x] An offline voice server makes a timer's create fail clearly

The endpoint is `/internal/relay/<server>/<path>` on the agent; the caller names itself and presents
its own /mcp credential, which the agent already holds to dial it, and may reach only its listed
server and path (`RelayedCalls`). The household is the home one (`HomeHousehold`, by
`OpenTunnel.Home`), in this phase. The deployment's timers already ring through the relay; voice must
run tunnelled (ticket 11) for them to ring in the deployment.
