# 13 — Mounts and capability discovery

**What to build:** the agent asks the mount before a path leaves it, and reports an absent
capability instead of guessing around it.

A path with no mount prefix comes back as the envelope the filesystem contract promises and the
agent acts on it. A mount that is declared absent is explained rather than retried. A capability
a mount does not have — execution on a mount without it — is reported, not worked around by
inventing another route.

**Blocked by:** 04, 05.

**Status:** ready-for-agent

- [ ] A turn naming an unprefixed path leads to the agent resolving the mount rather than
      guessing one.
- [ ] A declared-absent mount produces an explanation to the user and no retry storm.
- [ ] A request needing a capability the mount lacks is reported, and the agent does not
      substitute a different mount silently.
- [ ] A cross-mount transfer uses the single transfer call rather than a hand-rolled read/write
      loop.
- [ ] Every scenario cites its claims and was demonstrated red once.
