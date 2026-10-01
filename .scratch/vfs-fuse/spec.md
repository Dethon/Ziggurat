# Every mount is a filesystem to the shell

Status: ready-for-agent

## Problem Statement

The agent reaches its mounts by two routes, and the routes do not agree.

- **The file tools reach every mount.**
- **The sandbox shell reaches only the sandbox's own disk.** A command such as
  `grep -r TODO /vault`, `jq . /timers/<id>/status.json` or `rm -r /timers/<id>` fails with "No such
  file or directory". The other mounts are not in the sandbox container, and several have no files
  behind them at all: they are rendered on demand. The agent therefore has to know, mount by mount,
  which route works. The shell's whole toolbox (pipes, `jq`, `sed`, Python, any language it
  installs) is unavailable for most of what the agent manages.

The mounts that execute (timers, schedules, Home Assistant) offer closed catalogs of action files.
They are named like shell scripts (`dismiss.sh`) but are not scripts: they cannot be read, and they
cannot run from a shell or be composed with one.

Three existing gaps sit beside this work. The bridge this work introduces must not widen any of
them:

- **Every deployment secret is in every sandbox command's environment.** The sandbox container
  loads the deployment's whole secrets file, and commands inherit the server's environment. A
  command can print the model provider key, the Home Assistant token, the outpost secret and the
  rest.
- **The agent registration API has no authentication** and is proxied publicly.
- **Every MCP server accepts any caller,** and trusts whatever conversation context the call's
  metadata claims.

A sandbox command can reach all three today.

## Solution

Inside the sandbox, every non-outpost mount the calling session has appears as an ordinary
filesystem at its own **virtual path**. One FUSE filesystem serves them live. Every operation on it
is decided agent-side, through the calling session's registry and the same rules the file tools
obey. Any program that opens files (bash, Python, Node, compiled tools) works on any of those mounts,
with no work per language.

What a write *means* is still the mount's business. A write to a rendered file is refused or acted
on exactly as the matching tool call would be.

- **Outposts stay out.** They are presented as separate machines, addressed
  `outpost:<NAME>/...`.
- **Action files become extensionless executables** that run live from any script, with their real
  output and exit code.
- **Every exec result lists what the command changed** through the mounts: applied, refused or
  dropped. Bash swallows close errors, so this list is the agent's only reliable record.
- **The three existing gaps are closed in the same work.** Commands get a minimal environment, the
  registration API and every MCP server require a secret, and the sandbox server runs as a uid
  commands cannot inspect.

## User Stories

### The agent working through the shell

1. As the agent, I want `grep`, `find` and `wc` to work over the vault at `/vault`, so that I can
   search and aggregate notes with the tools I already know.
2. As the agent, I want `jq` and Python to read rendered status files at their virtual paths, so
   that I can compute over live state without a tool call per file.
3. As the agent, I want the paths in my commands to be the same virtual paths the file tools use,
   so that I never translate between two spellings.
4. As the agent, I want any language installed in the sandbox to work on the mounts, so that I am
   not limited to bash.
5. As the agent, I want `sed -i`, editors and atomic-write helpers to work on mount files, so that
   the safe way to rewrite a file is not the one that fails.
6. As the agent, I want a write through the shell refused exactly where the file tool would refuse
   it, so that the shell is not a way around a mount's rules.
7. As the agent, I want `rm -r /timers/<id>` to cancel the timer, as removing it with the file tool
   does, so that one action means one thing on every route.
8. As the agent, I want `mv` inside one mount to be that mount's move, so that its move rules
   apply.
9. As the agent, I want `mv` between two mounts to be a transfer that asks the move-out check
   first, so that a mount can refuse to lose a path however it is moved.
10. As the agent, I want `mkdir` followed by writing a file inside it to create both, so that
    ordinary scripts that prepare a directory work.
11. As the agent, I want a temporary file that my tool renames over its target to count as a write
    to the target, so that the temp file never appears on the mount.
12. As the agent, I want a file created earlier in my script to be visible to an action I run
    later in the same script, so that I can prepare input and act on it in one command.
13. As the agent, I want listings of rendered directories to show their real entries, so that
    `ls` and globs behave as on a disk.
14. As the agent, I want a rendered file's contents read in full even when its size is unknown in
    advance, so that a read never silently comes back empty.
15. As the agent, I want a listing too large to walk to be reported as truncated, so that I know
    `grep -r` saw only part of a mount.

### The agent and action files

16. As the agent, I want `/timers/dismiss` and Home Assistant actions to run from a bash or Python
    script with their real output and exit code, so that actions compose with the rest of the
    shell.
17. As the agent, I want action files to be refused on read and write on every route, so that I do
    not mistake them for scripts I can edit.
18. As the agent, I want the info and glob results to mark action files as executable, so that I
    can tell them apart without guessing from the name.
19. As the agent, I want one spelling of an action (`./dismiss`) to work whether or not my session
    has a sandbox, so that what I learned does not depend on the deployment.

### The agent reading the result

20. As the agent, I want the exec result to list every change my command made through the mounts,
    with its virtual path, operation and status, so that a close error bash swallowed does not
    hide a failed write.
21. As the agent, I want a refused change listed with the mount's own refusal, so that I can
    explain it or take another route.
22. As the agent, I want changes cut off by a timeout listed as dropped, so that I know a killed
    script left nothing half-written.
23. As the agent, I want the exec description to say which mounts are reachable from the shell and
    at which path, so that a mount whose name collides is still findable.

### The agent with no sandbox, and outposts

24. As the agent with no sandbox, I want `exec` on `/timers` to keep running the mount's own
    catalog, so that I can dismiss a timer without a shell.
25. As the agent, I want an outpost addressed as `outpost:<NAME>/...` and described as a separate
    machine, so that I never confuse a person's computer with a branch of my own tree.
26. As the agent, I want an outpost's exec to run on that machine with the scrutiny it has today,
    so that reaching a person's computer stays deliberate.

### A person

27. As a person, I want my own computer never reachable from a sandbox command, so that the
    sandbox's lighter exec screen cannot touch my files.
28. As a person, I want an operation that would need my approval as a tool call to be refused in
    the shell rather than run, so that a script cannot do what I would have been asked about.
29. As a person, I want my secrets out of reach of anything the model runs, so that a prompt
    injection cannot exfiltrate keys from the sandbox.
30. As a person, I want a timer dismissed from a script to stop ringing exactly as one dismissed by
    a tool call does, so that the effect does not depend on how the agent chose to do it.

### The maintainer

31. As the maintainer, I want the bridge reachable only with a token minted for one call, bound to
    one session and revoked when the call ends, so that the new endpoint grants nothing beyond the
    command it serves.
32. As the maintainer, I want the agent registration API to require a shared secret on every verb,
    so that the public proxy no longer exposes it.
33. As the maintainer, I want every MCP server to require the deployment secret, so that a forged
    conversation context needs a secret first.
34. As the maintainer, I want the sandbox MCP server to run as a uid commands cannot read, trace or
    kill, so that its copy of the secret stays its own.
35. As the maintainer, I want privileged work confined to a small root launcher, so that the
    process parsing every agent request stays unprivileged.
36. As the maintainer, I want each exec isolated in its own mount namespace with its own daemon, so
    that concurrent sessions never see each other's mounts.
37. As the maintainer, I want the sandbox granted only the device and capability FUSE needs, and an
    AppArmor change only if prod requires it, so that the container's privileges are the minimum.
38. As the maintainer, I want the integration suite to keep running in-process without root, so
    that the launcher does not make every test need a container.
39. As the maintainer, I want the new Rust crate to follow the existing crates' rules, so that the
    editors and the toolchain keep working across all three.

### Evaluation

40. As the eval author, I want a scenario family for shell-over-mounts, so that we know whether the
    model uses the new route or ignores it.
41. As the eval author, I want the timer, schedule and Home Assistant families re-run after the
    action-file rename, so that a renamed action the model no longer finds shows up as a red.

## Implementation Decisions

### What the agent sees

- **Every non-outpost mount of the calling session is served,** rendered mounts included. The
  sandbox's own disk stays as it is. Mount points come from each mount's identity, as today.
- **Outposts are readdressed and excluded.**
  - An outpost's virtual paths become `outpost:<NAME>/...`. Every other mount keeps
    `/<mount identity>`.
  - The registry resolves the new spelling, and the virtual-path translation produces it.
  - First-come mount claiming now only arbitrates between outposts, so a shadowed outpost can only
    be shadowed by another outpost.
  - The prompt describes outposts as separate machines.
  - An outpost's own exec keeps `Host` reach and its screening.
- **Action files lose their `.sh` suffix:** `dismiss`, `run_now`, and Home Assistant's service
  names.
  - They answer mode `0111` and are refused on read and write on every route.
  - The info result gains a defaulted "executable" flag, and glob marks action entries the same
    way, so other backends are untouched.
  - **`./dismiss` is the spelling taught**, because a real shell needs it in the action's
    directory. The catalog accepts it and the bare name alike.
  - Served actions (ADR 0036) follow the same rule.
- **`exec` on a mount without a shell** runs in the sandbox with that virtual path as cwd when the
  session has a sandbox. Otherwise it runs the mount's own catalog, as today. The file tools are
  unchanged.
- **The exec result gains a defaulted `VfsChanges` list.** Each entry carries a virtual path, an
  operation (`write` | `create` | `delete` | `move` | `action`), a status (`applied` | `refused` |
  `dropped`), and the mount's error envelope where there is one. Backends without a bridge leave it
  empty.

### Mechanism

- **Meaning lives agent-side.**
  - The daemon in the sandbox is a thin kernel adapter.
  - Resolution, parity, refusals, transfers and approval are decided agent-side, against the
    calling session's registry.
  - FUSE is one more client of the rules the file tools already obey.
- **The bridge** is a new agent HTTP endpoint, not proxied publicly.
  - One request per operation: attributes, listing, read (streamed from the mount's byte read),
    commit-write, delete, rename, action.
  - It is authenticated by a **call token**: minted by the exec tool, bound to the calling session's
    registry, its approval context and the set of mounts served (every non-outpost mount except the
    sandbox).
  - The token is revoked when exec returns, is cancelled or times out.
  - Every operation's outcome is appended to the token's change log, which becomes `VfsChanges`.
  - Minting, revocation and lookup are explicit calls, with no ambient state.
- **The token travels on the exec call's metadata,** beside the conversation context, the per-call
  data channel servers already parse. The backend exec signature and the reflected `fs_exec`
  schema do not change. The sandbox server hands the token only to the launcher. It never enters
  the command's environment or any file the command can read.
- **Privilege layout:**
  - A root **launcher** is the sandbox container's entrypoint. It owns the PUID/PGID switch the
    compose `user` setting owns today.
  - It starts the MCP server as a separate fixed uid, so a command cannot read that server's
    environment, trace it or kill it.
  - The server reaches the launcher over a unix socket that only its uid can open.
  - Per exec, the launcher:
    1. creates a private mount namespace;
    2. lazily unmounts the inherited `/vfs`;
    3. starts **one daemon** holding that call's token;
    4. runs `bash -lc <command>` as PUID (owner of the home volume), with supplementary groups kept,
       the bounding set dropped, and a **minimal environment** (home, path, timezone, locale,
       nothing from the secrets file);
    5. enforces the timeout with kill-tree;
    6. waits for the daemon's final commits;
    7. answers the exit code and the capped output.
  - The setuid bit is stripped from `fusermount3` in the image.
- **Two command runners agree on what a command sees.**
  - A launcher-backed runner is used where the launcher socket is configured, which is how the
    container runs.
  - The existing direct-process runner stays for in-process hosts (the integration fixtures), and
    passes the same minimal environment.
- **Placement:**
  - One FUSE filesystem at `/vfs`, with a `/<mount identity>` symbolic link into it per served
    mount. They must be links: bind mounts make a cross-mount rename fail as cross-device, which
    `mv` silently turns into copy-and-unlink (spike).
  - Empty system directories that collide are removed at image build (`/media`).
  - A mount whose identity collides with a non-empty image directory, or with `sandbox`, is served
    only at `/vfs/<name>`, and the exec description names it.
- **The Rust crate** (the launcher, the daemon on `fuser`, the action helper) is a third
  standalone crate under the root rules: no workspace, the same pinned toolchain, and listed in both
  editors' rust-analyzer project lists.

### Write semantics

- **Tool parity.** Each FUSE operation becomes the call the equivalent file tool would make, and is
  allowed only if that call would be:
  - a write to a file the mount reads as text → create with overwrite;
  - any other write → a blob write, only where the mount offers one;
  - unlink and rmdir → delete;
  - rename inside one mount → that mount's move;
  - rename across mounts → a transfer with move intent, so the move-out check runs first;
  - mkdir → exists only in the per-call cache until a file is created under it, which is then
    committed with directory creation.
- **Approval parity.** The bridge asks the question the equivalent `domain__filesystem__*` tool
  call would ask, against the agent's whitelist and remembered approvals.
  - An operation that would prompt is refused with `EACCES` and logged. A syscall never waits on a
    person.
  - FUSE operations are not screened. A rerouted exec takes the sandbox's reach (`Contained`).
- **Existing files commit on last close,** not on flush. Flush fires once per duplicated file
  descriptor, and `echo a > f` would otherwise commit an empty intermediate (spike).
- **A rename onto an existing path** is judged as a write to that path. The spike's `sed -i`
  replaced a refusing file this way.
- **New files are held in the per-call cache** until one of three things happens:
  - they are renamed onto a path, and commit as a write there;
  - an action runs, and every held file commits first;
  - the command exits, and whatever remains commits or is refused.

  This keeps temp-then-rename writers from ever committing their temp file.
- **Timeout and cancellation.** The token is revoked at the kill. Commits arriving afterwards,
  held files included, are dropped and logged as `dropped`. Operations already in flight finish and
  are logged.
- **Sizes.** Report the size from the mount's info when it has one. Otherwise serve the file with
  direct I/O. A guessed size of zero without direct I/O reads as empty, silently (spike).
- **Listing and cache.** A directory listing is the mount's glob `*`, with the trailing slash
  marking directories. Attributes are fetched lazily. Entries and attributes are cached for the
  call and dropped on any write through the bridge. The mounts' walk budgets bound recursive
  listings, and a truncated walk is logged.
- **Mode and ownership.** Files report `0644` and directories `0755`, owned by PUID:PGID so the
  kernel's permission check lets the command write. Whether a write is allowed is the bridge's
  decision, never the mode's.

### Action files

- **One static Rust ELF helper** is served, byte for byte, as every action file, with mode `0111`.
  The kernel executes it without read permission (spike), and it learns which action it is from
  its own executable path. It cannot be a shebang script, because the interpreter needs read.
- **The helper runs as PUID and holds no token.**
  - It sends its virtual path, argv and cwd to the per-exec daemon over a unix socket inside the
    namespace.
  - The daemon verifies the peer is a process of this exec, commits held files (the barrier), and
    calls the bridge's action operation: the mount's exec, with the action name and arguments as
    the command line.
  - The helper writes the action's stdout and stderr and exits with its code.
  - Nothing depends on the kernel's exec-open flag.

### Secrets

- **The agent registration API** requires a shared secret header on `GET`, `POST` and `DELETE`, in
  the outpost API's pattern:
  - a placeholder in the compose secrets file;
  - one comparison;
  - an unset secret refuses everything.
  
  Its only caller, SexyTime, must send it.
- **Every deployment MCP server's `/mcp`** requires one **deployment-wide secret.**
  - The hosting library installs the gate once for every server.
  - The agent reads the secret once from one bound setting, and applies it at session build to every
    configured endpoint's secret. The MCP client manager already sends that as `Authorization`.
  - Channel servers are included. Outposts keep their own secret.
  - A single secret was chosen over one per server. Accepted cost: a leak from any container opens
    every server, which is why the uid split and the minimal command environment matter.

### Deployment

- **The sandbox service gains** the FUSE device and the `SYS_ADMIN` capability. Without the
  capability the mount is refused (spike).
- **AppArmor:** the prod host is checked first, and a security option (a narrow profile before
  unconfined) is added only if the mount fails there.
- **Every stack that starts the sandbox image** gains the same device and capability: the E2E
  compose stack, the E2E sandbox fixture, and the eval's sandbox testcontainer.

## Testing Decisions

A good test here drives a public seam and asserts what a caller observes:
- the exec result and its `VfsChanges`;
- the bridge's answers;
- what a command running in the container can and cannot do;
- the kernel-visible outcome of a file operation.

It never asserts on how many internal calls were made, the cache's internals or the daemon's
private state. These four seams were agreed:

1. **The exec tool, with a scripted fake sandbox.** The fake sandbox stands in for the container:
   instead of running a command, it replays a scripted sequence of FUSE operations against the
   bridge, using the token the tool minted. Tests cover:
   - tool parity per backend kind: disk text, disk blob, rendered, refusing;
   - approval parity: whitelisted, remembered, would-prompt;
   - a transfer across mounts asking the move-out check, and a refusal keeping the source;
   - revocation on success, non-zero exit, timeout and cancellation;
   - dropped commits;
   - `VfsChanges` on the result;
   - routing of exec on a mount without a shell, with and without a sandbox;
   - the reach the screen sees for a rerouted exec.
   
   Prior art: the virtual-path conformance tests drive every tool against fake backends answering
   in hostile spellings; the transfer tests drive the two tools, never the module.
2. **The Rust daemon's core, with a fake bridge.** Kernel events go in, bridge requests come out,
   under `cargo test` with no kernel and no agent. Tests cover:
   - the commit state machine: last close, `echo a >`'s double flush committing once, rename-over
     as a write, held new files through rename, the action barrier and exit, revocation dropping
     everything later, mkdir held until a file lands;
   - cache invalidation;
   - the action-helper protocol.
   
   Prior art: the two existing crates' `cargo test` suites, which run with no .NET and no network.
3. **The real sandbox image, with a stub bridge.** The spike's cases become assertions:
   - a rename inside `/vfs` arrives as a rename, and cross-device happens only between the home and
     `/vfs`;
   - the action helper runs from bash and from Python's subprocess, and a read of it is refused;
   - rendered sizes read in full;
   - `sed -i` on a refusing file is reported as refused.
   
   It also asserts:
   - a command runs as PUID with no effective capabilities;
   - its environment holds no key from the secrets file;
   - it cannot read the server's process environment, `unshare`, or mount FUSE.
   
   Prior art: the E2E sandbox fixture and the testcontainer-based integration fixtures.
4. **Existing seams.**
   - The MCP server table and its contract tests gain the `/mcp` gate for every row, plus one
     refused-without-secret test per transport.
   - The registration API gets a test per verb.
   - The virtual-path conformance tests cover an outpost in the new spelling.
   - Prompt snapshots cover the rewritten descriptions and skills.
   - The eval gains a shell-over-mounts family (vault aggregation, a timer dismissed from a script,
     `sed -i` on a refused file read back from `vfsChanges`, a cross-mount `mv` between two mounts
     the scenario's agent has, an outpost path never touched from the sandbox).
   - Armed runs of the new family, and of the timer, schedule and Home Assistant families after the
     rename, compared against the last full scorecard. Back it up first, and never build during a
     run.

## Out of Scope

- **Outposts through FUSE,** now or later. They are separate machines by decision.
- **What FUSE can't offer:**
  - inotify on mounts: watchers never see a change the mount makes itself;
  - writable mmap of files served with direct I/O;
  - permission bits: `chmod +x` does nothing, so `bash /vault/x.sh` works and `/vault/x.sh` does
    not.
- **Making a FUSE read refuse a copy.** A status file "can be read and nothing else" (glossary). The
  copy tool refuses to copy one, but `cp` through the shell is a read the daemon cannot tell from
  any other, so a status file can be copied into the sandbox's workspace as a snapshot. Accepted.
- **SexyTime's own change** to send the registration secret: it lives outside this repo and is
  deployed alongside.
- **The media library through the shell for any shipped agent.** No shipped agent has both the
  sandbox and the library. The collision rule covers it generically, and no scenario exercises it.

## Further Notes

- **Spike.** The kernel-behaviour claims marked "spike" come from a throwaway prototype, committed
  out of `master` on branch `spike/fuse-sandbox` (two commits, ending `bb38da1c5`). Its findings file
  is the primary source:
  - the privileges FUSE needs;
  - symlinks against bind mounts for cross-mount renames;
  - executing a mode-`0111` ELF, and a shebang script failing;
  - per-exec namespaces;
  - rendered sizes;
  - close errors swallowed by bash;
  - the `sed -i` rename-over;
  - flush differing from commit.
  
  It was observed on WSL2 (kernel 6.18). The prod-host check re-runs its probe.
- **Decided 2026-10-01** in a grilling session, which also chose to ship everything on one branch
  from `master`.
- **Glossary.** Terms this work introduces, for the glossary when they land:
  - **action file**: an executable entry a mount serves, run and never read;
  - **bridge**: the agent endpoint that answers a sandbox command's file operations for one call;
  - **call token**: the bridge's per-call capability;
  - **held file**: a new file kept in the call's cache until it is renamed onto a path, an action
    runs, or the command exits.
  
  **Shadowed outpost** narrows to "shadowed by another outpost".
- **ADRs respected:**
  - 0015: the move-out check is asked before a cross-mount rename;
  - 0016: answers are in virtual paths;
  - 0025: FUSE mounts are not workspaces or landing targets;
  - 0027 and 0028: outpost endpoints and subagent inheritance are unchanged by readdressing;
  - 0036: served actions follow the rename;
  - 0039: skills that change ship with the server whose tools they teach.
