# 05 — The outpost serves a machine's filesystem

**What to build:** A single Linux binary that, when run on a machine, exposes that machine's
filesystem to the agent as an ordinary mount.

Copy the file onto a computer, run it with a name and a working directory, put its address into
an agent's configured endpoints by hand, and the agent can read, write, search, glob, move, copy
and remove files on that computer using exactly the same tools it uses on the vault and the
sandbox. Paths look the same. Refusals look the same.

This ticket is the filesystem only. It registers with nothing, it is not jailed, and it cannot run
commands. Both of those arrive next.

**Blocked by:** 01 — One allowed-extensions list.

**Status:** done

- [x] A new project in the solution, and a new row in the one server table, so it inherits the
      server contract tests, the filesystem conformance tests and the virtual-path conformance
      tests with no new test code.
- [x] The mount root is the machine's root, `/`, and the mount point is the name it was given.
- [x] Flags: name, working directory, listening port, and an extensions override. A flag the
      operator typed beats an environment variable of the same name, which the default
      configuration order does not give you for free.
- [x] The mount's description is generated from the values it was started with — the machine's
      name, the working directory, whether it is jailed, whether it can execute — so the prose
      cannot disagree with the behaviour. No separate prompt.
- [x] The working directory is the mount's declared workspace.
- [x] It builds as a self-contained single-file linux-x64 binary, not trimmed and not NativeAOT,
      and the published binary runs on a machine with no .NET installed.
- [x] It is the first server with no Dockerfile and no compose service. `CLAUDE.md` says so,
      because "every server is a container" is currently a safe assumption a reader would make.
- [x] Verified by hand: run the binary, add its URL to an agent's configured endpoints, ask the
      agent to read a file on that machine.
