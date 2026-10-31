# 10: Outposts on the tunnel

**What to build:** The outpost binary opens a tunnel with its own credential and serves its filesystem over it; the agent no longer dials it back. It keeps its flags, its `outpost:<NAME>` mount, jail and exec rules; `--advertise`, `--port` and the shared deployment secret go. It checks call tokens with the keys its tunnel delivers, and appears while its tunnel is open. Spec: `../spec.md` ("Outposts").

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] An outpost mounts, jails and executes through its tunnel, and is gone when the tunnel closes
- [ ] An outpost refuses an unsigned call
- [ ] The outposts' shared secret is gone from compose and `.env` (conformance)
- [ ] The outpost rules, docs and the glossary's outpost entries describe the tunnel
