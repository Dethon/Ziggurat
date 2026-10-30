# 02: The accounts service signs assertions

**What to build:** The accounts service vouches for who a turn is for. It generates its signing key on first start and keeps it in its volume, publishes its JWKS on the internal network only, answers every admitted session check with a fourth header carrying a signed assertion, and returns an assertion with every identity its internal resolution API resolves. The proxy copies the fourth header like the other three. A command rotates the key. Spec: `../spec.md` ("Issuers and verifiers", "Keys", "Channels and the agent").

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] The session check's assertion and the resolution API's verify against the service's JWKS and name the person, tenant, channel and roles
- [ ] The JWKS is reachable on the internal network and refused through the proxy on every host
- [ ] The conformance check asserts every protected route copies the fourth header
- [ ] Rotating adds a new signing key while the previous one stays published until retired
