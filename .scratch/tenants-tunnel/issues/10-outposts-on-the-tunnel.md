# 10: Outposts on the tunnel

**What to build:** The outpost binary opens a tunnel with its own credential and serves its filesystem over it; the agent no longer dials it back. It keeps its flags, its `outpost:<NAME>` mount, jail and exec rules; `--advertise`, `--port` and the shared deployment secret go. It checks call tokens with the keys its tunnel delivers, and appears while its tunnel is open. Spec: `../spec.md` ("Outposts").

**Blocked by:** 03

**Status:** done

- [x] An outpost mounts, jails and executes through its tunnel, and is gone when the tunnel closes
- [x] An outpost refuses an unsigned call
- [x] The outposts' shared secret is gone from compose and `.env` (conformance)
- [x] The outpost rules, docs and the glossary's outpost entries describe the tunnel

Notes:
- An outpost's tunnel is told apart from a household's own server by the **verified credential's
  server name**, `outpost-<NAME>` (`Domain/Outposts/OutpostServerName.cs`); `--name` must make a
  valid server name (lowercase label), checked at startup.
- The mount verdict (Mounted/Shadowed written back for the keepalive) is removed with the keepalive:
  one tunnel per (household, server name) makes the collision it reported impossible for a stock
  binary. `DialledEndpoints` became `McpClientManager.DialledServers` (`ServerAddress`).
- `CLAUDE.md` still says the outpost's "shared secret is never one of them" (a flag); it should read
  "tunnel credential" — left for the user, since CLAUDE.md is theirs to edit.
