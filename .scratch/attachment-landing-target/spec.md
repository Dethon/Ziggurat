# Attachment landing: a mount says where it can be written

Status: ready-for-agent

## Problem Statement

A person sends a file to the agent and the agent can look at it but never work on it. Where the
agent has a sandbox, an attachment is supposed to become a real file there — that is the whole
reason the sandbox is the only filesystem an attachment appears on — and nothing has ever
arrived.

Landing picks the exec-capable mount and builds its target directly under that mount point. The
sandbox mount is the container root, so every attachment targets a directory at the container
root. The image never creates it, the container runs as an unprivileged user, and the root is
owned by root. Every write fails.

Confirmed against the running stack: the sandbox container runs as uid 1000, its root is
`drwxr-xr-x root root`, and the target directory does not exist. Nothing provisions it, so no
attachment has ever landed on this deployment.

The failure is invisible. The write path logs a warning and returns false, the message records
an empty list of landed paths, and hydration adds nothing. The model is never told a file was
meant to be there, so it answers from the bytes as context and never mentions that the file it
might have run something against does not exist. The person sees an answer that quietly did
less than it could have.

Underneath is a knowledge gap rather than a typo. Landing finds its mount by asking which one
can execute, deliberately not by name, so it never depends on one server's spelling. That same
independence means it has no way to know that the one writable, persistent directory in that
container is the home directory. It picked the only location it could name, and that location
is unwritable.

## Solution

A mount says where it can be written. The writable, persistent directory under a mount — its
**workspace** — becomes something the mount publishes about itself, alongside its name, its
mount point and its prose. Landing asks the mount instead of knowing the answer, and puts the
attachment in the workspace, where the container user can write and the volume keeps it.

This restores what ADR-0021 already decided. That ADR names the target as
`~/uploads/<conversation>/<message-id>/<filename>` — the workspace — and the shipped code built
the path from the mount point instead. Nothing about the layout changes: a directory per
conversation, one per message, names never rewritten.

A mount with no workspace lands nothing, and says so. Where a file cannot be placed, the model
is told which files, through the same step that already tells it where the placed ones went, so
a turn that lost its files says so instead of planning commands against paths that do not
exist.

The sandbox stops asserting its own layout in prose that can drift from it. Its prompt is built
from the mount point and the workspace it was given, and the exec tool's description stops
naming any one mount's directories at all.

## User Stories

1. As a person sending a file to the agent, I want it to actually reach the agent's sandbox, so
   that asking it to run something against the file works.
2. As a person sending a file, I want it to still be there after the container restarts, so
   that a conversation resumed tomorrow can still act on what I sent today.
3. As a person sending a file, I want the agent to tell me when it could not take the file,
   so that I don't believe it worked on something it never had.
4. As a person sending two files with the same name, I want both to survive, so that the second
   one doesn't silently replace the first.
5. As a person, I want the file to keep the name I gave it, so that I can find it by that name
   and the agent can refer to it the way I do.
6. As a person, I want to be able to see and delete landed files in the volume I own, so that
   nothing accumulates somewhere I cannot reach.
7. As the agent, I want an attachment to appear as a real file in my sandbox, so that I can run
   a command against it rather than only describe it.
8. As the agent, I want the path I'm given to resolve through the filesystem tools, so that
   reading, moving or executing against it works without translation.
9. As the agent, I want the landing directory to be somewhere I can write, so that a command I
   run against the file can also write its output beside it.
10. As the agent, I want to be told which files could not be landed, so that I don't plan
    commands against paths that do not exist.
11. As the agent, I want to be told that within the same turn the files were sent, so that my
    first answer accounts for it rather than a later one.
12. As the agent, I want to stop being told about an old failure once the attachment itself is
    out of view, so that a months-old message doesn't keep reporting a file I could not have
    used anyway.
13. As the agent, I want the sandbox prompt to name the workspace the mount actually declares,
    so that the directory I'm told to work in is the directory that exists.
14. As the agent, I want the exec tool's description to stop naming one mount's home directory,
    so that guidance meant for the sandbox doesn't read as a rule for every filesystem.
15. As the agent using a mount that is not the sandbox, I want nothing about my mounts to
    change, so that this fixes one filesystem without disturbing the rest.
16. As a developer adding an exec-capable backend, I want to declare where it can be written
    once, so that landing works on my mount without anyone editing the landing code.
17. As a developer adding a backend with no writable area, I want declaring nothing to be
    valid, so that a script-dispatcher backend is not forced to invent a workspace.
18. As a developer, I want the workspace to be published in the backend's own coordinates, so
    that a backend states its mount point once and cannot contradict itself.
19. As a developer, I want the virtual path composed by the one translation that already exists,
    so that landing does not become a second place that joins mount points to paths.
20. As a developer, I want a mount that declares no workspace to land nothing rather than fall
    back to the mount root, so that the silent failure this work removes cannot come back
    through a default.
21. As a developer reading the mount contract, I want to know why a single-caller field exists,
    so that I don't replace it with a constant.
22. As a developer, I want a test to fail if the sandbox stops declaring a workspace, so that
    the mount's one behavioural claim is pinned.
23. As a developer, I want a test that fails if a file cannot actually be written in the built
    image, so that a permissions defect cannot ship green again.
24. As a developer, I want the sandbox's setting to be the single source of its workspace, so
    that the prompt, the declaration and the landing target cannot disagree.
25. As an operator, I want nothing to migrate, so that the existing volume and its contents stay
    where they are.
26. As an operator, I want no new volume or image change, so that a deployment picks this up by
    restarting rather than by being reconfigured.

## Implementation Decisions

**A mount publishes its workspace.** The filesystem backend base gains a hook for it, the
`filesystem://` resource carries it beside the name, mount point and description, and discovery
puts it on the mount record as a nullable value. This is the same route the mount's identity
already travels, so nothing new is invented for it. Recorded as ADR-0025.

Two alternatives were rejected. A literal in Domain would put one image's directory layout into
the layer built specifically not to have it — landing asks which mount can execute rather than
which mount is called "sandbox", and hardcoding the sandbox's home directory would undo that. An
agent-side setting naming the whole target would bypass the capability lookup, can disagree with
the compose volume, and breaks silently when the filesystem is renamed.

**The workspace is published in backend coordinates.** The backend answers `home/sandbox_user`,
not the virtual spelling, and Domain composes the virtual path through the one mount-point
translation. The backend asserts its own mount point once, in one field, so the two cannot
drift. ADR-0016's rule is untouched: the backend's spelling never reaches the model, because
composition happens before anything is reported.

**An exec-capable mount that declares no workspace lands nothing.** Falling back to the mount
root would preserve exactly the silent failure this work exists to remove. Failing the mount at
discovery would be too strong — three exec-overriding backends are script dispatchers with no
writable area and no business failing to mount over it.

**The layout under the workspace is unchanged.** A directory per conversation, one per message,
the person's own filename, and a second same-named file in one message separated one level
further down rather than renamed. The directory keeps the name `uploads`, which is what the
model already sees in the path.

**The sandbox's workspace comes from its existing home-directory setting.** The sandbox path
consistency work deletes that setting from the command runner's options, on the grounds that a
setting feeding only prose is worse than prose. That argument stops holding once the value
decides where a file is written, so the deletion is narrowed to the runner rather than performed
there and reversed here. That spec and its ticket have been amended.

**A landing failure is told to the model through hydration.** It names the files that could not
be placed, because failure is per file and a partly landed message is where a bare count would
make the model guess. It is recorded on the message alongside the landed paths, so the turn's
record is complete, but it is bounded by the hydration distance rather than inheriting the
unbounded life of a landed path: past that distance the model has neither the bytes nor the
file, and a notice about neither is noise. Landed paths keep their unbounded life, because the
file is still there.

**The sandbox prompt is built rather than asserted.** It takes the mount point and the workspace
and stops stating either as a literal. It currently spells the mount point eight times and the
workspace four, and the guard test introduced by the path-consistency work pins the mount point
to the image alias, not to the prose, so a rename would leave the prompt quietly wrong.

**The exec tool's description drops the sandbox specifics.** It is a compile-time constant in an
attribute and cannot interpolate, and it is the description the model actually reads — the
servers' own exec descriptions are filtered out while the domain tools are active. A
mount-agnostic tool naming one mount's home directory is what produced the duplicated literal;
the sandbox facts belong in the sandbox prompt. That constant is being rewritten by the
path-consistency work in any case, since it still claims the mount point defaults to a home
directory.

**Nothing sweeps a landed attachment.** The workspace already accumulates whatever the agent
writes there and nothing sweeps that either, so a landed attachment is no more transient than a
script the agent left behind. It is an ordinary file in a volume the person owns, visible and
removable. A second retention clock and a tie to the topic purge were both rejected.

**Glossary.** *Workspace* and *Landing* are recorded in the domain glossary. Landing is
deliberately distinct from the *upload store*, which is a holding area nobody works in and is
never a mount.

## Testing Decisions

A good test here asserts what a caller observes: where a file ends up, what the model is told,
and what a mount publishes about itself. None of them should reach into how the workspace is
plumbed, because the plumbing is the part most likely to be rearranged later.

Four seams, three of which are existing tests being extended.

**The sandbox landing unit tests are the seam for the Domain decision.** They already drive
landing against a recording sandbox through a real conversation group. They gain: the target
sits under the workspace the mount declared, and an exec-capable mount declaring no workspace
lands nothing and names the files it could not place. The existing collision and
virtual-path cases stand unchanged, which is the point — the layout is not what is changing.

**The hydration depth unit tests are the seam for the failure notice's lifetime.** They already
assert that an attachment within the distance reaches the model as content and beyond it becomes
a placeholder naming the file. The failure notice is the same kind of claim with the same
boundary, so it belongs beside them rather than in a test of its own.

**The filesystem server conformance theory is the seam for what a mount publishes.** It already
builds every filesystem server's real container from the one server table and asserts that
advertised tools, overridden operations and published capabilities are one set. It gains which
mounts declare a workspace — the sandbox alone — what the published resource carries, and that
the sandbox prompt names the mount point and workspace it was given rather than literals. This
is the only place a prompt is asserted anywhere in the codebase, and it earns that here because
the prompt has stopped being a constant.

**One new end-to-end test against the compose stack proves the file really lands.** It is the
only layer where the image, the volume and the unprivileged user are real: the in-process
sandbox fixture builds the server against a temporary root the test user owns, so it cannot
exercise a permissions fact at all, and that blindness is exactly why this defect shipped
green. It carries the E2E trait and reuses the sandbox stack introduced by the path-consistency
work, which lands first. It also covers discovery reading the published workspace end to end, so
no separate discovery seam is added.

Prior art: the virtual-path conformance theory is the model for asserting a contract across
every mount from one table, and the existing sandbox integration tests are the model for driving
the real server over the wire.

## Out of Scope

**Sweeping landed attachments.** Decided against, with the reasoning recorded above rather than
deferred.

**Making other mounts declare a workspace.** The vault and the media library have writable
roots, but nothing lands anything there and a declaration nobody reads is a claim waiting to go
stale. They declare nothing and behave exactly as today.

**Moving other prose onto the declared workspace.** The value now exists in one place, so other
text that happens to name it could derive from it later. Only the sandbox prompt and the exec
tool description are in scope here, because both are already being rewritten.

**The upload store.** It stays unmounted and reached only by naming a reference (ADR-0021).
Nothing here gives it a virtual spelling.

**The other exec-overriding backends.** They are script dispatchers with no writable area. They
declare no workspace and land nothing, which is the correct answer for them.

## Further Notes

This is a drift from a decision already made rather than a new design. ADR-0021 named the target
as the workspace and the code built it from the mount point; ADR-0025 records what the mount now
has to publish for the original decision to hold.

Nothing already written down changes meaning. Because no attachment has ever landed, no
conversation history contains a landed path, so there is no old spelling to keep working and
nothing to migrate. The volume, the compose service and the image are untouched.

The work is sequenced after the sandbox path consistency issues. They rework the exec and
home-directory semantics, rewrite the exec tool description and add the sandbox E2E stack that
this reuses. Nothing here conflicts with them; the sequencing exists so that two tickets do not
edit the same prompt.
