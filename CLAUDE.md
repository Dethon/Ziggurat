# Ziggurat

AI agent via Telegram/WebChat/MessageBus using .NET 10 LTS, MCP, and OpenRouter LLMs. The solution file is
`Ziggurat.sln`.

`satellite/` is `nabu-satellite`, a standalone Rust crate outside the .NET solution — read `satellite/CLAUDE.md` before touching it.

## Build, Test & Format

- `Tests/Unit` runs standalone. `Tests/Integration` and E2E tests (`[Trait("Category", "E2E")]`) need Docker, but their fixtures spin up the containers themselves (testcontainers for integration, the compose stack for E2E) — just run `dotnet test`; set `PLAYWRIGHT_HEADLESS=false` to watch the browser.
- The pre-commit hook (`.githooks/pre-commit`, wired via `core.hooksPath`) runs `dotnet format` over staged `.cs` files and re-stages them **whole** — partial/hunk staging does not survive a commit; make the working tree match the commit you want.

## Rules & TDD

`.claude/rules/*.md` are path-scoped (frontmatter `paths:`) and apply when touching matching files — coding style and layer rules, plus per-subsystem architecture notes (voice, printing, timers, scheduling, observability, memory, web browsing, Home Assistant, OpenRouter provider routing). Don't duplicate their content here.

Follow Red-Green-Refactor for all features and bug fixes: write a failing test first, watch it fail, then implement.

## Environment Variables

New configuration lives in `appsettings.json` / `appsettings.Development.json` by default. `DockerCompose/docker-compose.yml`'s `environment` block and `DockerCompose/.env` are not a mirror of every setting — they exist only for:

- **Secrets** (API keys, connection strings, credentials) — placeholder entry in `DockerCompose/.env` (never a real value), wired into `docker-compose.yml` as `${VAR_NAME}`.
- **Non-generic parameters** — inherently per-deployment values (a satellite's host IP, a topology-dependent URL) — a `docker-compose.yml` environment entry (placeholder like `changeme` where there's no safe default).

A new generic tunable (threshold, window, feature flag) belongs in `appsettings.json` **alone**. When adding code that reads a new setting, update whichever category applies in the same change.

## Multi-Agent Patterns

- **Stuck workers**: replace, don't wait — spawn a fresh agent for the same task. Never retry the same failing action more than twice; after two failures reassess or escalate.
- **Layer completion**: check `TaskList` for `completed` on every task in a layer before starting dependent work or reporting success. Never infer completion from partial signals.
- **Auto-commit** after each TDD triplet (RED → GREEN → REVIEW) succeeds, with a message referencing the triplet's feature.

## Local Development

Compose files, the launch command, secrets mounts, and WebChat/Dashboard access live in the `launch-stack` skill (`.claude/skills/launch-stack/SKILL.md`). Home Assistant setup lives in `.claude/rules/home-assistant.md`.

## MCP Server Hosting

Hosting contracts (`BindSettings`, `AddMcpHost`, `AddToolServer`, the error filter, the one server table) live in `.claude/rules/mcp-hosting.md`, loaded when touching `Mcp.Hosting/`, `McpServer*/` or `McpChannel*/`.

## Channel Architecture

Channel contracts (channel servers, `DeliveryPolicy`, conversation groups, connection lifecycle) live in `.claude/rules/channels.md`, loaded when touching channel code, `Domain/Monitor/`, `Domain/Channels/` or `Agent/`.

## Virtual Filesystem Architecture

VFS contracts (backends, capability-by-overriding, mounts, disk roots) live in `.claude/rules/virtual-filesystem.md`, loaded when touching `Domain/Contracts/`, `Domain/Tools/FileSystem/`, `Infrastructure/` or `McpServer*/`.

## Agent skills

### Issue tracker

Issues and specs live as tracked markdown under `.scratch/<feature-slug>/` — not GitHub Issues, despite the remote. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical roles, each label string equal to its name, written as a `Status:` line in the issue file. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `docs/adr/` at the root, plus a `CONTEXT.md` created lazily when a term needs pinning down. See `docs/agents/domain.md`.
