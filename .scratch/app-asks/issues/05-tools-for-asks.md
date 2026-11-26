# 05: Tools for asks

Spec: `.scratch/app-asks/spec.md` ("The app manifest gains three fields"; "What the agent does with an app turn").

**What to build:** The manifest's `asks` names servers. An app turn's session is built from the
building agent's servers that are in that list: their tools, mounts, prompt sections and skills, and
no others. Any call whose approval would be asked is refused at once with the usual rejection, and
nothing is sent to the anchor or offered on any channel. Sources of approval: the whitelist, the exec
screen, Jev, and as-person browsing.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] `AppManifest` takes `asks`: server names, shape-checked (origin seam).
- [ ] The model is offered only the declared servers' tools, and the file tools see only their mounts (agent seam, FakeOpenRouter).
- [ ] A server declared but not the building agent's is simply absent.
- [ ] An empty or absent `asks` means no tools.
- [ ] A whitelisted-ask call, an exec-screen ask and an as-person ask are each refused at once. No `request_approval` and no `offer_approval` is sent (agent seam).
- [ ] A call that never asks runs, acting as the viewer (agent seam).
