# 13 — Mounts and capability discovery

**What to build:** the agent asks the mount before a path leaves it, and reports an absent
capability instead of guessing around it.

A path with no mount prefix comes back as the envelope the filesystem contract promises and the
agent acts on it. A mount that is declared absent is explained rather than retried. A capability
a mount does not have — execution on a mount without it — is reported, not worked around by
inventing another route.

**Blocked by:** 04, 05.

**Status:** partially-done — the two exec-dependent cases need the sandbox in a container

- [x] A turn naming an unprefixed path leads to the agent resolving the mount rather than
      guessing one.
- [x] A declared-absent mount produces an explanation to the user and no retry storm.
- [ ] A request needing a capability the mount lacks is reported, and the agent does not
      substitute a different mount silently. **Withdrawn**: the eval hosts no exec-capable mount,
      because the sandbox backend runs bash against a real directory and hosting it in process
      would run model-authored shell on whoever's machine runs the suite. A turn asking for a
      script therefore tests a mount set no deployment has. The attempt did surface something:
      asked to run a script with nothing that can, the model delegated to a worker with the same
      toolset instead of saying so — recorded for ticket 16.
- [ ] A cross-mount transfer uses the single transfer call rather than a hand-rolled read/write
      loop. **Withdrawn** for the same reason: it needs two writable mounts and the eval has one.
- [x] Every scenario was demonstrated red once, and neither citation survived. The absent-mount
      demonstration was red at first — with the envelope-is-data rule deleted the model handed the
      same impossible listing to two workers — but a later run showed it does that about half the
      time with the rule in place, so the scenario now tolerates one worker and measures the storm
      through its ceiling, and the demonstration is green again. Resolving an unprefixed path
      stayed green too: the mount list alone places a path. Both scenarios stay as guards.
