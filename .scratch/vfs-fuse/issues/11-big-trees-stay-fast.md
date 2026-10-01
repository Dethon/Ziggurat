# 11 — Big trees stay fast

**What to build:** Recursive commands over a large mount stay practical. Entries and attributes are cached for the call and dropped on any write through the bridge, so `grep -r /vault` costs one listing per directory and one fetch per file rather than a round trip per stat. The mounts' existing walk budgets bound recursive listings, and a walk that stopped early is reported on the exec result, so the agent knows what a search did not see.

**Blocked by:** 07 — The shell reads every mount

**Status:** ready-for-agent

- [x] A recursive grep over a vault-sized tree makes at most one listing per directory and one attribute fetch per entry
- [x] A write through the bridge, followed by a read in the same command, sees the new content
- [x] A walk that hits a mount's budget is reported on the exec result as truncated
- [x] No cache outlives its call: a second exec sees changes made by the file tools in between
