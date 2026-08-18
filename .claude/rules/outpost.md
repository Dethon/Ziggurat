---
paths:
  - "McpServerOutpost/**"
  - "Domain/Outposts/**"
  - "DockerCompose/**"
---

# The Outpost

`McpServerOutpost/` is the one server with **no Dockerfile and no compose service**, because it is not part of the deployment: it is a self-contained single-file `linux-x64` binary somebody copies onto their own machine and runs with flags (`--name`, `--dir`, `--jailed`, `--exec`, `--hub`, `--advertise`, `--port`, `--ext`), publishing that machine's filesystem to the agent and registering itself with the hub. It ships no `appsettings.json` either, and it is the one server allowed to add a configuration source of its own — a flag the operator typed has to beat an environment variable of the same name, which the default order does not give you. The shared secret is the one value that is **not** a flag, because a command line is visible to every process on the machine; both ends present the same secret under different names, since only one of them binds it inside a section: the machine reads `SHAREDSECRET`, the hub reads `OUTPOSTS__SHAREDSECRET` (placeholder in `DockerCompose/.env`). It gates **both** directions — registration at the hub, and `/mcp` at the machine — and passing it as a flag is refused with a message rather than quietly accepted. "Every server is a container" is otherwise a safe assumption. See `docs/adr/0027` and `docs/adr/0028`.

`.claude/rules/virtual-filesystem.md` owns what an outpost is as a filesystem — the jail rule, `ExecutingOutpostFileSystem` being a separate type, mount shadowing and the verdict written back on each keepalive.
