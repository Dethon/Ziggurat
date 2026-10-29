# 02: Outposts register through the proxy

**What to build:** An outpost registers with the hub over TLS through the reverse proxy instead of the agent's plain port, so its secret never crosses the LAN in the clear. The main host gains an outpost-registration route to the agent; the exec bridge stays unrouted; the agent's dial-back to the machine is unchanged. The outpost documentation shows the proxy's address as the hub. Spec: `../spec.md` ("Outposts").

**Blocked by:** 01

**Status:** done

- [x] The conformance check asserts outpost registration reaches the agent and the bridge still doesn't
- [x] An outpost registering through the route is accepted with its secret and refused without
- [x] The outpost rules and README give the proxy's HTTPS address as the hub
