# 03 — An endpoint carries its origin

**What to build:** An MCP endpoint stops being a bare string once it reaches a running agent, and
becomes a value that says where it came from: configured, meaning it is named in the deployment's
own settings, or dynamic, meaning something registered it at runtime.

Nothing behaves differently yet. Every endpoint in existence is configured. This exists so the
next ticket can treat the two differently, and so that the code that later merges live outposts
into an agent's endpoint list has somewhere to put them.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] An endpoint value carries its address and its origin.
- [x] The agent spec and the thread session take the typed value; the agent and subagent
      definitions and the custom-agent registration keep plain strings, so `appsettings.json` is
      untouched and `mcpServerEndpoints` stays an array of strings.
- [x] The spec projection composes the typed values and marks everything it reads from
      configuration as configured.
- [x] No behaviour changes: sessions build from the same endpoints, in the same way, with the
      same failure modes.
- [x] The agent settings tests still pin the configuration file's shape unchanged.
