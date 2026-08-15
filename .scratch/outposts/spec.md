# Outposts: filesystems that register themselves

Status: ready-for-agent

## Problem Statement

Every filesystem the agent can reach is a container in the compose stack, configured into an
agent's endpoint list by hand and deployed with the rest of the stack. That is fine for the
vault, the media library and the sandbox, which are part of the deployment. It is useless for
the machines a person actually works on.

There is no way to say "the agent should be able to read and edit the files on this laptop,
starting now, for as long as this laptop is on". Doing it today means building a container
image for a machine that is not a container, exposing its disk into that container, adding an
endpoint to the agent's configuration, and redeploying the stack. Every one of those steps is
wrong for a machine that comes and goes, and the last one makes the agent's configuration
depend on which computers happen to be switched on.

## Solution

A single-file Linux executable, run on any machine, that publishes that machine's filesystem to
the agent and announces itself. It takes a name, a working directory and a flag saying whether
it is jailed to that directory, and it needs no entry in any configuration file: it registers
with the hub on startup, keeps its registration alive while it runs, and its registration
expires on its own when the machine sleeps, loses the network or is switched off.

An **outpost** is the resulting mount. To the model it looks like any other mount — the same
ten domain filesystem tools, the same virtual paths, the same refusal envelopes — and the only
thing that makes it different is that its existence is decided by the machine it lives on
rather than by the deployment.

## User Stories

1. As a person with a laptop, I want to run one binary on it and have the agent be able to read my files, so that I do not have to copy things into the sandbox to ask about them.
2. As a person running an outpost, I want to give it a name, so that I can tell my laptop from my desktop when I talk to the agent.
3. As a person running an outpost, I want to give it a working directory, so that its files land somewhere sensible and commands run where I expect.
4. As a person running an outpost, I want to jail it to that working directory, so that I can expose one project without exposing my whole home directory.
5. As a person running an outpost, I want to leave it unjailed, so that the agent can reach anywhere on the machine when that is what I want.
6. As a person running an outpost, I want a jailed outpost to refuse a path outside its working directory rather than silently returning nothing, so that I can tell refusal from absence.
7. As a person running an outpost, I want the binary to be one file with no sidecar configuration, so that installing it is a copy.
8. As a person running an outpost, I want it to take flags rather than environment variables, so that starting it by hand is one line.
9. As a person running an outpost, I want a flag I typed to beat an environment variable that happens to be set, so that the jail flag cannot be silently overridden.
10. As a person running an outpost, I want it to work out its own address, so that I do not have to know which interface the hub will reach it on.
11. As a person running an outpost on a multi-homed machine, I want to state its address explicitly, so that it registers the interface that actually works.
12. As a person running an outpost, I want it to listen on a documented default port, so that firewall rules are writable once.
13. As a person running an outpost, I want to override the port, so that I can run a jailed one and an unjailed one on the same machine.
14. As a person running an outpost, I want it to start serving even when the hub is unreachable, so that boot order and VPN timing do not decide whether my machine works.
15. As a person running an outpost, I want registration to retry forever, so that a hub that comes back finds my machine already there.
16. As a person running an outpost, I want it to deregister when I stop it, so that the agent stops offering a mount that has gone.
17. As a person running an outpost, I want to see locally why my machine is not showing up, so that a name collision is something I can diagnose without reading the hub's logs.
18. As a person running an outpost, I want to allow command execution explicitly, so that exposing files does not imply exposing a shell.
19. As a person running an outpost, I want command execution to be off unless I ask for it, so that the safe thing is the thing that happens by default.
20. As a person running an outpost, I want to override which file extensions count as text, so that a machine full of unusual source files is still readable.
21. As the person who owns the hub, I want a shared secret to gate registration, so that anyone on my network cannot attach a machine to my agent.
22. As the person who owns the hub, I want the agent to present that secret when it dials, so that anyone who can reach the port cannot use my agent's filesystem tools.
23. As the person who owns the hub, I want an outpost registration to expire on its own, so that nothing has to notice that a machine died.
24. As the person who owns the hub, I want registrations to survive an agent restart, so that a container recycling does not silently drop every machine.
25. As the person who owns the hub, I want to choose which agents can see outposts, so that the download assistant does not gain access to my laptop.
26. As the person who owns the hub, I want an outpost whose name collides with an existing mount to lose, so that a stranger's machine cannot shadow my vault.
27. As the person who owns the hub, I want to know from the metrics when an outpost registered, was refreshed and expired, so that I can answer whether a machine was up at a given time.
28. As a person talking to the agent, I want a machine that is asleep to cost me only its own mount, so that one closed lid does not take away the vault and home automation.
29. As a person talking to the agent, I want a container that is down to fail loudly, so that a real outage is not hidden as a missing mount.
30. As a person talking to the agent, I want the model to know a mount's working directory and whether it is jailed, so that it does not waste a turn discovering the rule by being refused.
31. As a person talking to the agent, I want an outpost registered mid-conversation to appear without me restarting anything, even if it takes until the next session.
32. As a person sending an attachment, I want it to land in the sandbox, so that a laptop being connected does not change where my files go.
33. As the model, I want an outpost to answer in virtual paths like every other mount, so that a path from one tool can be passed into another.
34. As the model, I want the same ten domain filesystem tools on an outpost, so that there is nothing new to learn per machine.
35. As the model, I want a jailed outpost's glob and text search to be rooted at the working directory, so that a search does not exhaust its budget on a disk it cannot read anyway.
36. As the model, I want a refused path to say it was refused, so that I can tell a jail from an empty directory.
37. As a developer, I want the outpost to be a normal project in the solution, so that it inherits the server contract tests and the filesystem conformance tests.
38. As a developer, I want the outpost and the sandbox to share their backend code, so that a filesystem operation cannot behave differently depending on which one answers it.
39. As a developer, I want the allowed-extensions list to exist once, so that the sandbox and the outpost cannot drift apart on what counts as text.
40. As a developer, I want the register-keepalive-expire loop testable without a machine, so that the lifecycle is provable in unit tests.
41. As a developer, I want the expiry proven against a real Redis, so that a wrong TTL argument cannot ship green.

## Implementation Decisions

### The executable

- A new .NET project, `McpServerOutpost`, inside `Ziggurat.sln`, referencing `Domain`,
  `Infrastructure` and `Mcp.Hosting` like every other tool server. It is row fourteen in the one
  server table, so it inherits `McpServerContractTests`, the filesystem conformance tests and the
  virtual-path conformance tests without new test code.
- Published as a self-contained single-file linux-x64 binary. **Not** NativeAOT and **not**
  trimmed: `AddFileSystemTools<TBackend>` decides a server's tool set by reflecting over which
  operations the backend overrides, and the MCP SDK generates tool schemas reflectively. The
  binary size is the price of the outpost behaving identically to the sandbox.
- It is the first server with no Dockerfile and no compose service. `CLAUDE.md` gains a line
  saying so, because "every server is a container" is currently a safe assumption a reader would
  make.
- Configuration arrives as command-line flags: `--name`, `--dir`, `--jailed`, `--exec`, `--hub`,
  `--advertise`, `--port`, `--ext`. `BindSettings` appends environment variables and user secrets
  on top of what the host builder assembled, and command-line arguments sit at the bottom of that
  stack, so the flags must be re-applied above the environment source. A `Jailed` environment
  variable beating `--jailed` is the specific failure this avoids.
- The shared secret is the one value that does not arrive as a flag; it is an environment
  variable, with a placeholder in the compose env file wired through as `${VAR}` on the hub side.

### The backend

- `OutpostFileSystem` extends the existing text disk root, adding exec only when `--exec` was
  given. Capability is declared by overriding, so the exec override must be conditional on
  construction rather than the tool being conditionally registered — resolve this by having the
  outpost register one of two backend types, since the registrar reflects over the type.
- **The mount root is always `/`.** The jail is not a different root; it is a refusal rule, in the
  same shape as the media library's refusal: one predicate every operation asks before it acts.
- A jailed outpost refuses any path argument outside `--dir`, and roots the glob walk and the text
  search walk at `--dir` rather than walking from `/` and filtering. Walking `/` would spend the
  50,000-entry scan budget on directories it is going to discard, and would report
  `budgetReached` for a reason the model cannot see.
- `--dir` is the mount's declared workspace and the exec working directory.
- `DescribeMount` is generated from the actual parameter values: the machine's name, the working
  directory, whether it is jailed, whether exec is available. No separate prompt type. Generating
  it means the prose cannot disagree with the behaviour.
- The allowed-extensions list moves out of the sandbox's settings file into a shared constant in
  `Domain` that both servers read, with `--ext` replacing it wholesale for one machine.

### Registration

- The outpost hosts its MCP endpoint and then POSTs a registration to the Agent, which already
  hosts an HTTP API and already does runtime registration for custom agents. The Agent writes one
  Redis key per outpost registration. **The outpost never touches Redis**, so nothing new is
  exposed on the network.
- Keepalive is a periodic PUT that refreshes the key's TTL. Thirty second interval, ninety second
  expiry. A clean shutdown sends a DELETE so a stopped outpost disappears immediately rather than
  ninety seconds later.
- **The registration's name is its identity, and the last write wins.** A machine that restarts
  re-registers over its own entry, which is the common case and needs no special handling. Two
  machines sharing a name steal the mount from each other; that is accepted.
- Expiry is Redis's own TTL. There is no reaper service, no sweep and no timer on the hub.
- The advertised address is autodetected from the route toward the hub, and `--advertise`
  overrides it. Neither yielding a usable address is a startup failure with a message, not a
  silent registration of something unreachable.
- Both directions present one shared bearer secret: the outpost when registering and keeping
  alive, the agent when dialling.
- The Agent publishes a metric when a registration lands, is refreshed and expires. The outpost
  itself reports no telemetry.

### Consumption

- A new per-agent setting opts an agent into outposts. Configured agents that want them get the
  flag; the download assistant does not. Nothing is opted in by default.
- Outposts join an agent's endpoint list when a thread session is built, and are discovered and
  mounted by the existing filesystem discovery with no changes. A registration therefore takes
  effect at the next session build, consistent with the decision that a server's tool set is
  fixed for a connection generation.
- **`AgentSpec.McpServerEndpoints` stops being `string[]`.** An endpoint becomes a record
  carrying its origin, configured or dynamic. The typing stops at the spec boundary: the agent
  and subagent definitions and the custom-agent registration keep `string[]`, because they bind
  straight from `appsettings.json` and retyping them would turn `mcpServerEndpoints` into an
  array of objects in every agent profile to express a distinction configuration cannot make.
  `AgentSpecProjection` composes the typed endpoints, marking configured ones as it goes, which
  is also where dynamic ones are later merged in.
- The dial policy reads that origin. A configured endpoint that fails to dial fails the session,
  exactly as today. A dynamic endpoint that fails to dial is logged, dropped, and its mount is
  simply absent for that session. Recorded as ADR-0027.
- Static mounts are mounted before outposts, so an outpost whose name collides is refused
  deterministically and the existing mount always wins. The collision is not checked at
  registration time, because mount names live inside each server's filesystem resource and are
  only read during session build.
- The verdict — mounted, or shadowed — is written back onto the registration and returned in the
  next keepalive response, so the person at the machine can see why their outpost is not
  appearing. This is the only feedback channel to the machine.

### Landing

- A mount gains a **landing target** claim, published in its filesystem resource beside the
  workspace, saying whether attachments may be put into it. The sandbox declares yes; an outpost
  declares no.
- `AttachmentLanding` asks for that claim instead of asking which mount can execute. Asking for
  exec was sufficient while the sandbox was the only executing mount; an exec-enabled outpost
  would otherwise start receiving a person's attachments. ADR-0025 carries a note recording the
  refinement — the principle is unchanged, the mount still answers for itself and landing still
  never reasons from a name.

## Testing Decisions

A good test here drives external behaviour through the seam a caller actually uses, and asserts
on what comes back: a refusal envelope, a mount list, a landed path, a key that is gone. It does
not assert that a particular method was called, and it does not reach into the registry's storage
to check a shape. The jail tests in particular must go through the tool boundary rather than the
backend, because that is where the virtual-path rule lives and a refusal leaking a backend
coordinate would otherwise pass.

Four seams, three of which already exist.

**The outpost registry** is the one new seam. Drive the registry type directly over an injected
store and `TimeProvider`, the way the agent definition provider is tested rather than its
endpoint: register, refresh, expire, re-register over a live entry, deregister, record and read
back the mount verdict. The Agent's three HTTP endpoints stay one-liners over it with nothing of
their own to test. One integration test on the shared Redis fixture proves the TTL is real and
the key actually disappears, because the TTL is the whole expiry mechanism and a wrong argument
would otherwise ship green.

**The domain filesystem tools** are the seam for jail behaviour. Point an outpost backend at a
temporary directory and drive the read, glob, search, remove and transfer tools through it,
asserting refusals with the shared envelope assertions. Prior art is the existing per-tool test
suite. Cover: a jailed outpost refusing a path above its working directory, refusing an absolute
path elsewhere on the machine, rooting a glob at the working directory rather than at `/`, and an
unjailed outpost allowing all of the same. The conformance suites are inherited from the server
table row and need no new code.

**Thread session construction** is the seam for the dial policy. The existing thread session test
and its MCP server fixture are the prior art; a dead endpoint is an unused port. Assert that a
dynamic endpoint that cannot be dialled leaves a session built from the rest, that a configured
one in the same position fails the session, and that a dynamic failure does not delay or affect
the mounts that did come up.

**Attachment landing** is the seam for the landing target. It already takes a registry and
returns an outcome. Build a registry holding a landing-target sandbox and an exec-capable outpost
that declares no landing target, and assert the file lands in the sandbox. Assert the inverse
too: with only a non-landing exec mount present, nothing lands and the attachment is named as
failed, which is the behaviour ADR-0025 already established for a mount with no workspace.

The end-to-end path — publish the binary, run it, register, dial it over real HTTP, kill it,
watch the mount go — is not automated. It is the thing most likely to break, and it is bought at
the cost of a publish step in CI and a slow platform-bound test. Verify it by hand for the first
release and revisit if it breaks twice.

## Out of Scope

- Windows and macOS outposts. Linux x64 only.
- Any transport other than the outpost listening and the hub dialling it. An outpost behind NAT,
  on a different network, or reachable only outbound does not work, and making it work means a
  reverse transport, which is a separate piece of work.
- Mid-session mounting. A registration never appears inside a session that is already built, and
  an expiry never removes a mount from one.
- Outposts in the dashboard. Registration events reach the metrics; there is no view of them.
- Telemetry from the outpost itself. The keepalive stays a liveness ping and does not grow a
  reporting schema.
- Per-outpost access control finer than the per-agent opt-in. An agent that can see outposts sees
  all of them.
- Encryption of the shared secret in transit. It crosses a local network in plaintext, which is
  the same exposure as the MCP traffic itself.
- Automatic installation, packaging or a service unit for the binary. It is a file you copy and
  run, supervised however the machine's owner chooses.

## Further Notes

The glossary terms are in `CONTEXT.md` under Outposts: outpost, outpost registration, jailed
outpost, shadowed outpost, plus landing target under Virtual filesystem. Use them in code and in
prose; in particular an outpost that lost a name collision is *shadowed*, not rejected, because
its registration is perfectly valid and only the mount did not happen.

`PathJail` already exists and means containment against a configured root. It is a different
thing from an outpost's jail, which refuses paths without moving the root. The glossary term is
deliberately "jailed outpost" rather than "jail" to keep the two apart.

The two-tier dial policy will look like an inconsistency to anyone reading the dial code, and the
obvious refactor is to unify it. ADR-0027 exists to be found at that moment.
