# 15: SexyTime behind the same sign-in

**What to build:** SexyTime users sign in like everyone else and their custom agents are theirs. The proxy checks the session in front of SexyTime; the agent registration API takes the person from the verified headers and ignores any user parameter; custom agents are keyed by user id (still held in memory). The agent-API secret stays for SexyTime's server. Spec: `../spec.md` ("Keyed by user id", Custom agents).

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] A custom agent registered through the API belongs to the person in the verified headers; a user parameter is ignored
- [ ] A person sees and uses only their own custom agents
- [ ] The conformance check asserts SexyTime's host goes through the session check
