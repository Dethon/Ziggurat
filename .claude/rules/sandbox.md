---
paths:
  - "McpServerSandbox/**"
  - "Infrastructure/Clients/Bash/**"
  - "Domain/Tools/Files/SandboxFileSystem.cs"
  - "sandbox-runtime/**"
---

# The Sandbox

`McpServerSandbox/` is the deployment's own Linux container where the agent runs commands (`fs_exec`, `bash -lc`). `.claude/rules/virtual-filesystem.md` owns what it is as a mount (workspace, landing target, `Contained` reach).

- **A command gets a minimal environment.** The container is started with the deployment's whole secrets file (`env_file: .env`), and a child process inherits its parent's environment by default, so a command could otherwise print the model provider key, the Home Assistant token or the outpost secret — the cheapest exfiltration a prompt injection has. `CommandEnvironment.Minimal` is an **allowlist** (home, path, timezone, locale), so a secret added to the file later is excluded without anyone remembering to exclude it; `HOME` is the workspace the mount publishes, never the server's own. `BashRunnerOptions.Environment` carries it; null inherits, which only the outpost uses, because there the command runs on its operator's own machine in the environment they started it with. Process credentials are not environment: the `render` supplementary group still reaches commands, so GPU access is unchanged.
- **Commands run through a root launcher.** The image's entrypoint is `sandbox-runtime/`'s launcher (read its `CLAUDE.md`): it starts the server as a uid of its own, so a command cannot read the server's environment, trace it or kill it, and runs every command as PUID — the home volume's owner — in a private mount namespace with the bounding set dropped and no-new-privs. Compose therefore has no `user:` for the sandbox and grants `SYS_ADMIN` for the namespace; every stack that starts the image does the same through `SandboxContainer.AsCompose()` (the E2E fixture and the eval's testcontainer). The server reaches the launcher through `LauncherRunner` when `LAUNCHERSOCKET` is set, which only the launcher sets; an in-process host (the integration fixtures) keeps `BashRunner`, and both resolve the working directory through the same `CommandCwd` and hand the command the same minimal environment. Two uids share the home volume: it is kept group-writable with PGID, and what the server's file tools create is handed to PUID before each command.
