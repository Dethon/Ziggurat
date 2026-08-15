# 10 — An opted-in agent mounts live outposts

**What to build:** The payoff. Turn on a machine, run the binary, and the next time you talk to an
agent that is allowed to see outposts, that machine's files are there. Turn the machine off and
they are gone. Nothing was edited, nothing was redeployed.

Which agents can see outposts is a deliberate choice, not a default. An agent that exists to
search for downloads has no business reaching a person's laptop, and a new machine appearing on
the network must not silently widen what any agent can touch.

**Blocked by:** 03 — An endpoint carries its origin. 04 — A dynamic endpoint that fails to dial is
dropped. 08 — The hub records a registration and lets it expire. 09 — The outpost registers itself
and keeps itself alive.

**Status:** ready-for-agent

- [ ] A per-agent opt-in setting. Nothing is opted in by default; the general-purpose and voice
      assistants are turned on, the download assistant is not.
- [ ] For an opted-in agent, live outpost registrations join its endpoint list as dynamic
      endpoints when a session is built. The existing filesystem discovery mounts them with no
      changes.
- [ ] A registration takes effect at the next session build, and an expiry likewise. Nothing
      mutates a session that has already been built. This is the same rule ADR-0012 sets for a
      channel server's tool set, for the same reason.
- [ ] Configured mounts are mounted before outposts, so an outpost whose name is already some
      other mount's is shadowed: it is not mounted, the existing mount is untouched, and the fact
      is logged. Which outpost loses is decided by mount order, not by timing.
- [ ] A name collision is not checked when the registration is announced, because mount names
      live inside each server's filesystem resource and are only read when a session is built.
- [ ] An outpost that is asleep costs only its own mount, which is ticket 04 doing its job here.
- [ ] Verified end to end by hand: a machine appearing and disappearing while a conversation is
      ongoing, and the agent picking it up on the next session.
