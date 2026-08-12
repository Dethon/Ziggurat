# Sandbox paths: one spelling for arguments, commands and results

Status: ready-for-agent

## Problem Statement

The agent is taught one rule for filesystem paths: start every path with a mount point. The
filesystem prompt states it twice, once as a requirement and once as a warning against bare
container paths, and the registry enforces it — a path that names no mount is rejected before
anything else happens.

The exec tool honours that rule for its working-directory argument and breaks it everywhere
else. The sandbox mount is the container root, so the mount point is a name the registry
knows and the container has never heard of. A command that spells a path the way the agent
was taught fails against bash with "no such file or directory". The agent has to know that
the prefix is required in the argument and forbidden three characters later in the command
string, and nothing tells it so.

The same seam splits the other way on the results. Exec reports the directory a command ran
in as a container path with no mount point. Feeding that straight back into another
filesystem call — the obvious next move, and the one every other tool supports — fails at the
registry with "no filesystem mounted". Paths that come out of the commands themselves have
the same shape and the same problem.

Underneath both is a third disagreement. The mount point means the container root to glob,
read, search and every other tool, and means the home directory to exec alone. One mount, two
meanings, decided by which tool is being called.

## Solution

The mount point becomes a real name for the container root inside the container, so a path
spelled the way the agent was taught resolves whether it appears as an argument or inside a
command. Both spellings work, because they name the same place.

The working directory an exec reports comes back in the same coordinates as every other path
in every other response, so it can be used as-is in the next call.

Exec stops treating the mount point as the home directory. It means the container root there
as it does everywhere else, and the guidance that used to be a special case in the resolver
moves into the prompt: the workspace is the home directory under the mount, writes elsewhere
fail because the container runs as an unprivileged user, and paths appearing in command output
are container-native and need the mount point in front of them before they are used as paths.

## User Stories

1. As the agent, I want a path I write inside an exec command to resolve the same way as a path
   I pass as an argument, so that I don't have to hold two spellings for one filesystem.
2. As the agent, I want the mount-prefixed spelling the filesystem prompt teaches me to be the
   spelling that works inside a command, so that following the instruction isn't the cause of
   the failure.
3. As the agent, I want a container-native path I already have to keep working inside a command,
   so that a path taken from earlier output doesn't have to be rewritten before it can be used.
4. As the agent, I want the working directory an exec reports to be usable directly as a path
   argument, so that acting on where a command ran doesn't cost a failed call first.
5. As the agent, I want the mount point to mean the same directory in exec as it does in glob,
   read, search and info, so that one name doesn't have two meanings.
6. As the agent, I want the container root to be addressable, so that I can run a command there
   when I mean to rather than being redirected somewhere else.
7. As the agent, I want the exec tool to tell me that paths in command output are
   container-native, so that I prefix them before reuse instead of discovering it through an
   error.
8. As the agent, I want to be told where the writable workspace is and that writes outside it
   fail, so that I choose a working directory deliberately now that one is no longer chosen for
   me.
9. As the agent, I want an absolute path passed as a working directory to stay inside the mount,
   so that a working directory cannot silently leave the coordinate frame the response is
   expressed in.
10. As the agent, I want a cross-mount path inside a command to fail as an ordinary shell error,
    so that a path that cannot work reads as wrong rather than being rewritten into something
    else that is also wrong.
11. As the agent, I want the whole rule stated in the exec tool's own description, so that I
    learn it from the contract rather than inferring it from examples.
12. As the person using the assistant, I want the agent to stop spending turns on paths that
    don't resolve, so that a request involving running something finishes sooner.
13. As the person using the assistant, I want the agent's account of where it ran a command to
    name a path I can also reach through the filesystem tools, so that I can check its work.
14. As the person using the assistant, I want paths recorded in past conversations to keep
    resolving, so that nothing I already have written down stops working.
15. As a developer adding an exec-capable backend, I want the coordinate rule for the working
    directory stated on the result type I have to fill in, so that I don't have to infer it by
    reading what other backends happen to do.
16. As a developer, I want the reason a field stopped being exempt recorded where the decision
    lives, so that the exemption isn't reintroduced by someone reading the older text.
17. As a developer, I want a test to fail when a tool answers the working directory in backend
    coordinates, so that the rule holds without anyone having to remember it.
18. As a developer, I want the mount point and the container alias pinned to each other by a
    test, so that renaming the filesystem fails a build rather than breaking commands in
    production.
19. As a developer, I want one setting for the sandbox's root, so that two values cannot
    disagree about the same directory.
20. As a developer, I want the workspace path to live where it is documentation rather than
    where it looks like a tunable, so that nobody changes a setting and believes they have moved
    the directory.
21. As an operator, I want the alias to be part of the image, so that a deployment cannot come
    up without it.
22. As an operator, I want the change to leave the persistent volume and its contents where they
    are, so that nothing has to be migrated.

## Implementation Decisions

**The mount point becomes a real alias inside the container.** The sandbox image gains a
symlink from the mount point to the container root. This states a truth that already holds —
the sandbox mount *is* the container root — so both spellings resolve to the same place with
no translation anywhere.

Two alternatives were rejected. Rewriting the command text to strip the prefix needs
shell-aware parsing: any rule loose enough to catch every case corrupts literal data in quoted
strings, heredocs, `sed` patterns and embedded scripts, and any rule tight enough to be safe
misses cases. Relocating the container's filesystem under a real directory cannot work at all,
because a process's root is always spelled `/` — chrooting into the new location puts you back
where you started, and moving the files without chrooting stops binaries loading, since the ELF
interpreter path is an absolute string.

**The mount stays at the container root rather than the workspace.** Mounting the home
directory instead was considered, because it would make the root and the default working
directory the same place and dissolve the two-meanings problem. It was rejected: it turns the
rule for reusing a path from command output from "prefix the mount point" into "strip the home
prefix, then prefix the mount point", and it leaves everything outside the workspace with no
virtual spelling at all. That reintroduces the original defect in a harder form.

**Working-directory resolution becomes total.** Every path is resolved against the configured
container root. The branch that took an absolute path verbatim is removed, so a working
directory can no longer escape the root; with the root at `/` this is identical in production
and closes the hole everywhere else. The branch that mapped an empty or dot path to the home
directory is removed with it.

**The default working directory is deleted rather than kept.** Exec now reads the mount point
as the container root, as every other tool does. The guidance that special case encoded —
work in the persistent workspace — moves into the prompt and the tool description, where it is
advice the agent can follow rather than a rule that makes one tool disagree with the rest.

The accepted cost: a command that writes a relative file without naming a working directory
now lands at the container root and fails as an unprivileged user, where it previously
succeeded in the workspace. This was chosen over keeping a benign exception, because the
exception cost the agent a second rule for the same mount point.

**The working directory is normalised at the backend and translated at the tool.** The command
runner reports it relative to its own configured root — the only component that knows that root
— and the exec tool prefixes the mount point using the shared translation already used for glob
entries and search hits. Neither side has to guess at the other's frame, and correctness stops
depending on the sandbox root happening to be `/`.

The root itself reports as the mount point with a trailing slash, which is how glob already
spells it and what ADR-0016 says a trailing slash means. That round-trips faithfully now that
the empty path no longer means the home directory.

**The home-directory setting is deleted** from the command runner's options. It has no
behaviour left once the default is gone. The workspace path stays a literal in the prompt and
tool descriptions, where it is documentation about the image.

Deleting it from the sandbox server's settings and appsettings as well was the original
intent, on the grounds that a setting which only feeds prose is worse than prose. That no
longer holds: `attachment-landing-target` 01 makes the same value the workspace the sandbox
mount declares, which decides where an attachment is written (ADR-0025). It stays on the
server, unread until that ticket lands.

**ADR-0016 is amended in place.** Its exemption list goes from two fields to one, recording why
the working directory became claimable and why a single field was normalised at the backend
after normalising backends was rejected as a blanket policy. The result type carries the
invariant in its own documentation, where someone writing a new backend will read it.

**Documentation carries what the code stopped encoding.** The exec description states that
either spelling works inside a command, that the working directory argument is used literally,
and that paths in command output are container-native and need the mount prefix before use as a
path. The sandbox prompt states that the mount point is the container root, that the persistent
workspace is the home directory beneath it, and that writes elsewhere fail as an unprivileged
user.

**Scope is the sandbox.** Three other backends override exec, but they match a literal script
name and answer 127 for anything else. They take no path arguments inside commands and cannot
have this defect.

## Testing Decisions

A good test here asserts what a caller observes: the coordinates of the paths in a response,
and the directory a command actually ran in. None of them should reach into how resolution is
implemented, because the whole change is a rearrangement of that implementation behind an
unchanged set of observable answers.

Three seams, two of which are existing tests being edited rather than anything new.

**The VFS virtual-path conformance theory is the seam for the agent-facing half.** Deleting the
working-directory entry from its exemption list makes the existing theory assert the rule
against a backend answering in three deliberately hostile spellings, for every tool derived
from the operation list. No new test is written. This is the highest seam in the change and it
is the test ADR-0016 built for exactly this purpose.

**The command runner's unit tests are the seam for the backend half.** They already drive real
bash against a configured root. Two existing cases assert the removed default and change to
assert that empty and dot paths resolve to the container root. Two are added: the reported
working directory is root-relative, and an absolute path is clamped under the root rather than
escaping it. This is the only seam that reaches those edges directly.

**One new end-to-end test against the compose stack proves the image alias.** It is the only
layer where the Dockerfile is real — the in-process sandbox fixture constructs the server
against a temporary root and cannot see the image at all. It carries the E2E trait and drives
the compose stack, as the existing end-to-end tests do. A guard test pins the mount point
string to the alias so a rename fails a test rather than breaking commands in production.

The sandbox integration tests are deliberately untouched. They call the server tool directly,
below the exec tool and the registry, so asserting the working-directory contract there would
duplicate the runner's unit tests over the wire without covering the prefixing. Their existing
cases pass unchanged: the one that passes an empty working directory runs an inline interpreter
command that does not care which directory it runs in.

Prior art for the safety of the alias already exists: the local filesystem client's integration
tests assert that symlinks inside a tree are neither followed nor listed, which is the property
that keeps globbing bounded once the alias is added.

## Out of Scope

**Cross-mount paths inside a command.** A vault or media path written into a sandbox command
names a different container with no shared volume and can never work. It stays an ordinary
shell failure. Diagnosing it would require the very command parsing this change exists to
avoid.

**Rewriting command output.** Paths emitted by `pwd`, `find`, `which` and everything else stay
container-native. They cannot be rewritten for the same reason command text cannot, and the
mitigation is the documented prefixing rule.

**Backend error prose.** A working directory that does not exist is reported by the backend
naming the resolved container path, with no mount context on the far side of the wire.
ADR-0016 already puts this out of scope, and nothing here changes it.

**The unbounded glob at the sandbox root.** Globbing the mount point walks the entire container
twice with no depth cap, no entry cap and no timeout, and the cancellation token is accepted
and ignored. This predates the change and is unaffected by it. Filed separately.

**Attachment landing.** Attachments target a directory at the container root that the image
never creates and that the unprivileged container user cannot write to. Confirmed against the
running stack — nothing has ever landed — and filed separately as
`attachment-landing-target` 01, which is sequenced after this work.

**The other exec-overriding backends.** They are script dispatchers, not shells.

## Further Notes

The alias is safe for the file tools, which was the one thing that could have killed it. The
recursive enumeration skips reparse points by explicit option, so the walk of the container
root never traverses the new link; the path jail resolves symlinks by walking path components
rather than following link chains, so it terminates on the alias and a genuine cycle surfaces as
a denial rather than a hang.

Nothing changes spelling. The mount point still means the container root, so every path already
written down in a past conversation or a note resolves exactly as it did before.
