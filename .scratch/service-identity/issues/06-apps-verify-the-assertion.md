# 06: Apps behind the proxy verify the assertion

**What to build:** A container on the internal network can no longer forge a person to the apps. The chat hub, Sugary, the dashboard and the agent registration API verify the signed assertion against the accounts service's keys and take the person from it, not from the plain headers alone; a missing or invalid assertion is signed out. The registration API takes the assertion SexyTime's server forwards, beside its secret, and the SexyTime change is noted for its own repository. Spec: `../spec.md` ("Apps behind the proxy").

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] A request with valid plain headers but no assertion is treated as signed out by each app
- [ ] A request whose assertion names one person and whose plain headers name another acts for the assertion's
- [ ] The registration API refuses a call with the secret but no valid assertion
- [ ] The note for SexyTime's repository says what to forward
