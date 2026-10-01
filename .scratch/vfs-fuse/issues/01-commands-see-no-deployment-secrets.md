# 01 — Commands see no deployment secrets

**What to build:** A sandbox command runs with a minimal environment: home, path, timezone and locale, and nothing from the deployment's secrets file. Today the sandbox container loads the whole secrets file, and every command inherits it, so a command can print the model provider key, the Home Assistant token or the outpost secret. After this, it cannot. The sandbox MCP server keeps whatever configuration it needs; only what reaches a command changes.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

- [x] A command's `env` contains none of the keys in the deployment's secrets file
- [x] A command still has a working home, login path, timezone and locale (`bash -lc` finds user-installed tools)
- [x] The render group membership still reaches commands, so GPU access is unchanged
- [x] Output caps, timeout and kill-tree behave as before
- [x] The sandbox's architecture rule notes that commands get a minimal environment, and why
