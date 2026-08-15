# 02 — A mount declares whether it accepts landings

**What to build:** A mount states for itself whether attachments may be put into it, and the
landing code asks that question instead of asking which mount can run commands.

Today landing finds its target by looking for the mount with the exec capability, which worked
while the sandbox was the only mount that could execute anything. An outpost is a filesystem on a
person's real machine and may be able to execute, so without this change the first exec-enabled
outpost silently starts receiving that person's attachments.

The sandbox declares that it accepts landings. Nothing else does. Behaviour for every existing
conversation is identical.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A backend declares whether it is a landing target, the filesystem resource publishes the
      claim beside the workspace, discovery reads it and the mount carries it.
- [ ] `AttachmentLanding` selects its mount by the landing-target claim, not by the exec
      capability.
- [ ] An attachment sent to an agent with the sandbox mounted lands exactly where it lands today.
- [ ] With only an exec-capable mount that declares no landing target present, nothing lands and
      the attachment is named to the model as one it cannot act on — the same behaviour ADR-0025
      established for a mount with no workspace.
- [ ] The landing tests drive `AttachmentLanding` through a registry, asserting on the outcome,
      and cover both directions above.
- [ ] `CONTEXT.md`'s **Landing target** term is used in the code and the prose.
