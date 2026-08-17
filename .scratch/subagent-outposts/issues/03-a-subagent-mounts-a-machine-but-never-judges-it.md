# 03 — A subagent mounts a machine but never judges it

**What to build:** The verdict a machine reads off its own keepalive keeps meaning one thing. An
outpost is told it is mounted, or that its name was already taken and it is shadowed, and that
answer comes from the agent it registered with. A subagent's endpoint list is not its parent's, so
the same machine can be mounted in one session and shadowed in the other; without this, whichever
built last is what the person at the machine sees, with nothing saying that some delegated task
decided it.

After this ticket a subagent mounts outposts exactly as before and writes nothing back. The
machine's verdict is whatever its parent's session build recorded.

**Blocked by:** 02 — A subagent reaches outposts when both say so.

**Status:** ready-for-agent

- [ ] The agent spec carries whether this build records mount verdicts: true from the agent entry
      point, false from the subagent one. A field with a value, like every other difference between
      an agent and a subagent.
- [ ] The build step reads that field where verdicts are recorded and never asks which kind of agent
      it is building.
- [ ] Endpoint composition is unchanged. A subagent still receives the names of the outposts it was
      given; it simply writes nothing back.
- [ ] Unit tests at the projection that the flag is true for an agent and false for a subagent.
- [ ] A test that a subagent's session build leaves an existing registration's verdict as it was,
      driven through the seam that records them rather than by reaching into the registry's storage.
- [ ] Nothing is reused for this — the existing history flag is not overloaded to mean two things.
