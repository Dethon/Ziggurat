# 0028 — A subagent inherits outposts only if it also asks

Status: accepted
Date: 2026-08-17

## Context

`UsesOutposts` is a top-level agent's opt-in: an agent named in `appsettings.json` says whether
live outposts — filesystems on real machines — join its endpoint list when a session is built.
Nothing is opted in by default (ADR-0027 covers what happens when one of those endpoints will
not answer).

`AgentSpecProjection.ForSubAgent` set the flag to `false` unconditionally, on the grounds that
inheriting it would widen what a subagent can touch without anybody having said so. The result
is that delegation loses the machines. `jonas-worker` exists to be "the same toolset as Jonas
for delegating tasks", Jonas has `usesOutposts: true`, and the worker it spawns to do the work
is the one thing in the deployment that cannot see the laptop the work is about. A person who
asks Jonas to go through a project on their desktop gets a parent that can read it and a
subagent that cannot, which is not a policy anybody chose — it is the default falling out of
the projection.

## Decision

A subagent sees outposts when **both** its parent's `UsesOutposts` and its own
`SubAgentDefinition.UsesOutposts` are true. Both default to false, so nothing is opted in by
default at either level, and neither one alone is enough.

What is inherited is the flag, not a set of machines. The subagent's own session build asks the
registry, so it mounts whatever is live at spawn time rather than replaying what its parent saw
— the same rule that says an outpost registered mid-conversation appears at the next session
build, applied to a session that happens to belong to a subagent.

An inherited outpost is the whole mount, `fs_exec` included where the machine was started with
`--exec`. Capability is declared by overriding on the backend, so there is no way to say "this
mount can execute, but not for this caller" without inventing a per-caller capability rule that
exists nowhere else in the virtual filesystem.

A subagent mounts outposts but never records a mount verdict on their registrations. The verdict
is the machine's one feedback channel, and it answers "did the agent you registered with mount
you". A subagent's endpoint list is not its parent's, so it can reach a different verdict for the
same machine; letting it write would make the operator's keepalive report a collision caused by
some delegated task, with nothing saying which agent decided it.

## Considered options

**The parent's flag alone flows down.** One flag, one place to get it wrong, and a subagent
already runs under the parent's conversation, whitelist and user. Rejected because a subagent is
not a copy of its parent: it names its own `mcpServerEndpoints`, and the list of subagents is
shared by every agent that enables the feature. Under this rule, adding a narrow worker to the
file would silently hand it every registered machine the moment any opted-in agent spawned it.

**The subagent's flag alone.** Symmetrical with how a subagent's endpoints already work, and
each definition would state its own reach. Rejected: it lets a subagent reach a machine its own
parent cannot, which inverts the relationship — a subagent acts on the parent's behalf and
cannot be more privileged than the thing that spawned it.

**Inherit the parent's resolved endpoints as a snapshot.** The subagent would see exactly the
machines the parent mounted, with no second registry lookup and no window where a machine
disappears between the two builds. Rejected: it is the only place in the repo where a session's
mounts would come from another session rather than from the registry, and it buys nothing —
the parent's successful dial does not transfer, the subagent dials its own clients regardless,
and a machine that has gone is dropped by ADR-0027 either way.

## Consequences

- A subagent's mounts can differ from its parent's within one turn: the parent built its session
  earlier, and a machine that registered or expired in between shows up on one side only.
- `IAgentFactory.CreateSubAgent` takes a `SpawnContext` — the conversation, the user, the
  whitelist patterns and the outposts flag — rather than a growing list of positional parameters.
  Every one of them is "what the parent was", and the next such value belongs in the record
  rather than appended to a signature.
- `AgentSpec` carries `RecordsOutpostVerdicts` so the suppression is a field with a value, like
  every other difference between an agent and a subagent, rather than a check for which one is
  building.
- A parent whose whitelist pre-approves `domain__filesystem*` pre-approves it for the subagents
  it spawns, so a delegated task can run commands on an exec-enabled machine with no approval
  prompt. That is the parent's whitelist doing exactly what it says; the lever for narrowing it
  is the whitelist, not the mount.
