# Subagents and outposts: delegation that reaches the machine

Status: ready-for-agent

## Problem Statement

An agent that is allowed to see outposts loses them the moment it delegates. A person asks the
assistant to go through a project on their desktop; the assistant can read it, hands the job to
the worker it spawns for exactly that kind of work, and the worker comes back saying there is no
such mount. The machine is live, its registration is fine, and the only reason the work cannot be
done is that a subagent's outposts were switched off in code rather than by anybody's decision.

Today `AgentSpecProjection.ForSubAgent` hardcodes the opt-in to false. That was a defensible
default when it was written — inheriting a parent's reach without anybody saying so is exactly
the kind of silent widening the outposts work set out to avoid — but the result is a rule nobody
can change from configuration, and a delegating agent whose delegate is strictly less able than
itself for one class of mount and no other.

## Solution

A subagent's definition gains its own outposts opt-in, and a subagent sees outposts when its
parent and its own definition **both** say yes. Both default to false, so a machine is reached
by delegation only when somebody said so twice: once on the agent that talks to the person, and
once on the worker that does the job.

What a subagent inherits is the opt-in, never a set of machines. Its own session build asks the
registry, so it mounts whatever is live at the moment it is spawned, on exactly the terms every
other session gets: dynamic endpoints that will not answer are dropped, a name that collides with
an existing mount is shadowed, and an outpost started with command execution allowed offers it.

The one thing a subagent does not do is write back. Mount verdicts stay the answer to "did the
agent you registered with mount you", and a delegated task's collision set is nobody's business
at the machine.

Recorded as ADR-0028.

## User Stories

1. As a person with a laptop and a delegating assistant, I want the subagent it spawns to reach my machine, so that a task big enough to delegate is not a task the delegate cannot do.
2. As a person talking to the agent, I want delegation to be invisible, so that whether my request was answered directly or by a worker does not change which files were reachable.
3. As a person talking to the agent, I want a subagent to be no more able than the agent I am talking to, so that delegating cannot reach somewhere I could not have reached by asking directly.
4. As the person who owns the hub, I want a subagent's access to my machines to be a deliberate choice, so that adding a worker profile does not quietly hand it every registered laptop.
5. As the person who owns the hub, I want that choice stated on the subagent as well as on its parent, so that one narrow worker can be kept off the machines while another is allowed on them.
6. As the person who owns the hub, I want both settings to default to off, so that an omitted flag always means no at either level.
7. As the person who owns the hub, I want a subagent whose parent is not opted in to see no outposts however its own definition is written, so that the parent remains the ceiling.
8. As the person who owns the hub, I want the general-purpose worker turned on, so that the delegation path the deployment actually uses reaches the machines its parents can already reach.
9. As the person who owns the hub, I want the setting named the same on an agent and on a subagent, so that reading one profile teaches me the other.
10. As a person running an outpost, I want a subagent's session build not to change what my keepalive tells me, so that "mounted" and "shadowed" keep meaning what they meant.
11. As a person running an outpost, I want a mount verdict to come from the agent I registered with, so that a collision inside somebody's delegated task is not reported to me as my name being taken.
12. As a person running an outpost with command execution allowed, I want a subagent to be able to run commands there, so that the machine behaves the same whoever is asking.
13. As a person running an outpost, I want a subagent to reach my machine only while my registration is live, so that switching the machine off ends delegation's access at the same moment it ends everybody else's.
14. As a person running an outpost that registered after a conversation started, I want a subagent spawned afterwards to see it, so that a machine that came up mid-conversation is usable without waiting for the parent's next session.
15. As a person running an outpost that expired, I want a subagent spawned afterwards not to see it, so that a stale mount is not kept alive by delegation.
16. As a person whose machine is asleep, I want a subagent's session to build without it, so that one closed lid does not fail a delegated task that never needed that machine.
17. As the model, I want a subagent's outpost mounts to look like every other mount, so that there is nothing about delegation to learn per machine.
18. As the model, I want a subagent to be told its mounts' workspaces and jail rules the same way its parent is, so that it does not spend a turn discovering a refusal rule.
19. As the model, I want a name collision inside a subagent's session resolved the same way as in its parent's, so that the deployment's own mounts always win.
20. As a developer, I want the parent-and-subagent rule to live in the projection, so that the difference between an agent and a subagent stays a field with a value rather than a check downstream.
21. As a developer, I want everything the parent contributes to a spawn to travel as one value, so that the next such value is added to a record rather than appended to a signature.
22. As a developer, I want verdict suppression expressed as a field on the spec, so that the build step never asks which kind of agent it is building.
23. As a developer, I want the rule provable without a machine, so that both flags and their combinations are covered in unit tests.
24. As a developer, I want one test that spawns a subagent against a live outpost, so that a flag being right in the spec is not mistaken for a mount that actually appears.
25. As a developer reading the dial code, I want the reason a subagent's reach is two flags rather than one to be findable, so that the obvious simplification is not applied by somebody who has not seen the argument.

## Implementation Decisions

### The rule

- `SubAgentDefinition` gains an outposts opt-in, named as it is on an agent definition and
  defaulting to false. It binds from the subagent profiles in settings like every other field on
  that type.
- The projection's subagent entry point computes the effective opt-in as the conjunction of the
  parent's and the definition's. The hardcoded false and the comment justifying it are replaced;
  the comment's concern is answered by the definition's own flag, not abandoned.
- The general-purpose worker profile in the shipped settings turns it on. Both agents that enable
  the subagents feature are already opted in, so the change is live for chat and for voice on the
  same edit.

### The spawn

- `IAgentFactory.CreateSubAgent` takes the definition, the approval handler and a **spawn
  context**: a record carrying the conversation, the user, the whitelist patterns and the parent's
  outposts opt-in. It replaces four positional parameters that were already "what the parent was",
  and it is where the next such value goes.
- The spawn context is a plain value in the agent domain namespace, beside the agent key and the
  disposable agent — not in the settings-binding namespace, because nothing binds it from
  configuration.
- The factory's subagent delegate composes the context from the spec it is already building
  against, so the parent's flag reaches the projection without any new lookup.
- Nothing changes in the subagent tool or in the feature config the tool is given. The delegate's
  signature stays "a definition in, a running agent out"; the parent's contribution is captured,
  as the conversation and whitelist already are.

### Mounting

- No change to endpoint composition, the dial policy or discovery. A subagent's session build asks
  the registry through the same path an agent's does, and ADR-0027 applies unchanged: a dynamic
  endpoint that will not answer is logged and dropped, a configured one still fails the session.
- Configured endpoints stay first, so an outpost whose name collides with one of the subagent's
  own mounts loses, exactly as in a parent's session.
- Inherited outposts are the whole mount, command execution included where the machine allows it.
  Capability is declared by overriding on the backend, so a per-caller carve-out has nowhere to
  live; narrowing it is the parent's whitelist's job.

### Verdicts

- `AgentSpec` gains a flag saying whether this build records mount verdicts: true for an agent,
  false for a subagent. The build step reads it where verdicts are recorded and never asks which
  kind of agent it is.
- Composition is unaffected — a subagent still receives the names of the outposts it was given, it
  simply writes nothing back.

## Testing Decisions

A good test here drives the seam a caller actually uses and asserts on what comes back: a spec
field, a mount list. It does not assert that the projection was called with particular arguments,
and it does not reach into the factory to observe the spawn.

Two seams, both existing. No new seam is needed: the rule is a projection decision, and its effect
is a mount.

**The agent spec projection** is where the rule is proved. Its existing unit suite already asserts
subagent projection field by field — the feature stripping, the fresh routing session, the parent's
conversation, the empty patchable model list — and the new cases sit beside them: both opt-ins true
yields an opted-in spec; parent true and definition false yields opted-out; parent false and
definition true yields opted-out; both false yields opted-out; and the verdict-recording flag is
true from the agent entry point and false from the subagent one. The shipped-settings binding suite
gains an assertion that the worker profile's opt-in is bound from configuration and that an omitted
one is false.

**Outpost mounting** is where the effect is proved. The existing integration suite already composes
endpoints against a registry and a real outpost fixture and asserts which mounts appear, including
the shadowing case. One test there spawns a subagent from an opted-in parent and asserts the live
outpost is among its mounts. Prior art for the spawn itself is the existing subagent integration
test and its fake agent factory, which follows the changed contract.

The unit suite alone would prove the flag and not the mount; the integration test alone would fail
without saying which of the two flags was wrong. Both, in that order, red first.

## Out of Scope

- A subagent spawning subagents. The feature is still stripped from a subagent's enabled features,
  and nothing here changes that.
- Per-outpost access control for subagents. A subagent that can see outposts sees all of them,
  which is the rule for agents too.
- Suppressing command execution on an inherited outpost. Considered and rejected in ADR-0028; the
  lever is the parent's whitelist.
- Passing the parent's resolved endpoints down as a snapshot. Considered and rejected in ADR-0028;
  the subagent asks the registry.
- Mid-session mounting. A subagent's mounts are fixed for its session, like every other session.
- Any change to registration, keepalive, expiry, the shared secret or the outpost binary. The
  machine cannot tell a subagent from an agent and is not being taught to.
- Telling the model, in the subagent tool's description, which machines a worker can reach. Mounts
  describe themselves during discovery.
- A dashboard view of which agent or subagent mounted a machine.

## Further Notes

The glossary entry for **subagent** in `CONTEXT.md` now carries the rule: it reaches outposts only
when its parent and its own definition both allow it, so it is never more privileged than the agent
that spawned it. The outposts terms — outpost, outpost registration, jailed outpost, shadowed
outpost — are unchanged and apply to a subagent's mounts exactly as to an agent's.

There is no term for "an outpost a subagent inherited", deliberately. Nothing in the model
distinguishes it from any other mount, and naming it would invite code that treats it differently.

ADR-0028 records the two-flag rule and exists to be found by whoever notices that one flag would
be simpler. The parent-alone and subagent-alone alternatives are written down there with the
reasons they lose, because both will be proposed again.

A parent whose whitelist pre-approves the filesystem tools pre-approves them for its subagents,
so a delegated task can run commands on an exec-enabled machine with no approval prompt. That is
the shipped configuration today for both opted-in agents. It is a consequence of this change, not
a defect in it, and the lever for narrowing it is the whitelist.
