---
paths:
  - "McpServerSandbox/**"
  - "Infrastructure/Clients/Bash/**"
  - "Domain/Tools/Files/SandboxFileSystem.cs"
---

# The Sandbox

`McpServerSandbox/` is the deployment's own Linux container where the agent runs commands (`fs_exec`, `bash -lc`). `.claude/rules/virtual-filesystem.md` owns what it is as a mount (workspace, landing target, `Contained` reach).

- **A command gets a minimal environment.** The container is started with the deployment's whole secrets file (`env_file: .env`), and a child process inherits its parent's environment by default, so a command could otherwise print the model provider key, the Home Assistant token or the outpost secret — the cheapest exfiltration a prompt injection has. `CommandEnvironment.Minimal` is an **allowlist** (home, path, timezone, locale), so a secret added to the file later is excluded without anyone remembering to exclude it; `HOME` is the workspace the mount publishes, never the server's own. `BashRunnerOptions.Environment` carries it; null inherits, which only the outpost uses, because there the command runs on its operator's own machine in the environment they started it with. Process credentials are not environment: the `render` supplementary group still reaches commands, so GPU access is unchanged.
