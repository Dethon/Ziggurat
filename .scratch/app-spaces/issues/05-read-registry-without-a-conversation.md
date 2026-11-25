# 05: Prefactor: a read registry without a conversation

Spec: `.scratch/app-spaces/spec.md` ("The app read API (the agent)"; Further Notes).

**What to build:** Today every VFS registry is built inside an agent session. Extract the build so the agent can make a registry for (household, agent definition, person) outside any conversation. It must dial the definition's servers withheld by features and own-server state exactly as a turn's are, and sign each call as that person of that household. Nothing outside the agent changes and turns behave the same. This makes the app read API (06) an easy change.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A turn's session builds its registry through the extracted builder, and existing VFS and session tests stay green unchanged.
- [ ] A registry built for a person outside a conversation mounts the same mounts a turn of that agent would, with the same features and own servers withheld.
- [ ] Calls through it are signed for that person and household, and a per-person mount (`/sugary`) answers that person's rows.
- [ ] A household server going offline or reconnecting is seen at the next build, as a turn sees it.
