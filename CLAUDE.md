# Ziggurat

AI agent via Telegram/WebChat/MessageBus using .NET 11, MCP, and OpenRouter LLMs.

Three standalone Rust crates sit outside the .NET solution and share no build with each other or with it — read each one's `CLAUDE.md` before touching it. `satellite/` is `nabu-satellite`, the Wyoming voice satellite. `speech-typist/` is the Windows speech typist: a person holds a key and their words are typed into whatever window was already in front, posting at Lemonade directly rather than through this stack (`docs/adr/0026`). Its `cargo test` must keep passing with no .NET project built and no Lemonade reachable. `sandbox-runtime/` is the sandbox container's privileged half — the root launcher that is its entrypoint — built inside `McpServerSandbox/Dockerfile`; its `cargo test` must pass with no root and no Docker.

**Every server is a container, except one.** `McpServerOutpost/` has no Dockerfile, no compose service and no `appsettings.json`, because it is not part of the deployment — it runs on somebody's own machine, configured by flags, and its tunnel credential is never one of them. The contract lives in `.claude/rules/outpost.md`, loaded when touching `McpServerOutpost/`, `Domain/Outposts/` or `DockerCompose/`.

There is no Cargo workspace and no `Cargo.toml` at the root, deliberately — the crates pin different targets and must not share a build. The cost is that rust-analyzer sees several projects and refuses to pick one, so `.vscode/settings.json` and `.zed/settings.json` name every crate in `linkedProjects`; a new crate goes in both lists. The crates do pin the **same toolchain**, and should keep doing so: an editor runs one proc-macro server, and pinning them apart makes serde's derives fail to expand in whichever crate loses, which reads as errors the compiler never sees.

## Build, Test & Format

- `Tests/Unit` runs standalone. `Tests/Integration` and E2E tests (`[Trait("Category", "E2E")]`) need Docker, but their fixtures spin up the containers themselves (testcontainers for integration, the compose stack for E2E) — just run `dotnet test`; set `PLAYWRIGHT_HEADLESS=false` to watch the browser.
- A bare `dotnet test` spends money only on the two **canaries** (`Tests/Canary/`); every other test talks to `FakeOpenRouter`, and `PaidCallsConformanceTests` fails any test outside `Tests/Canary`, `Tests/Eval` and `Tests/Smoke` that reads user secrets — details in `.claude/rules/testing.md`.
- The **smoke tier**, the **behavioural eval** and its **research** and **Sugary** tiers spend real model calls or need a running stack: run them only when asked, through the `test-tiers` skill (commands, env vars, scorecards). The smoke stack always runs on 127.0.0.1 by the deployment's hostnames — never point it, or the stack, at the deployment's host.
- The pre-commit hook (`.githooks/pre-commit`, wired via `core.hooksPath`) runs `dotnet format` over staged `.cs` files and re-stages them **whole** — partial/hunk staging does not survive a commit; make the working tree match the commit you want.

## Rules & TDD

`.claude/rules/*.md` are path-scoped (frontmatter `paths:`) and apply when touching matching files — coding style and layer rules, plus per-subsystem architecture notes (voice, printing, timers, scheduling, observability, memory, web browsing, Home Assistant, OpenRouter provider routing, the outpost). Don't duplicate their content here.

Follow Red-Green-Refactor for all features and bug fixes: write a failing test first, watch it fail, then implement.

## Environment Variables

New configuration lives in `appsettings.json` / `appsettings.Development.json` by default. `DockerCompose/docker-compose.yml`'s `environment` block and `DockerCompose/.env` are not a mirror of every setting — they exist only for:

- **Secrets** (API keys, connection strings, credentials) — placeholder entry in `DockerCompose/.env` (never a real value), wired into `docker-compose.yml` as `${VAR_NAME}`.
- **Non-generic parameters** — inherently per-deployment values (a satellite's host IP, a topology-dependent URL) — a `docker-compose.yml` environment entry (placeholder like `changeme` where there's no safe default).

A new generic tunable (threshold, window, feature flag) belongs in `appsettings.json` **alone** — unless several hosts must agree on its value, in which case it belongs in a **shared policy file**: one file shipped beside its settings type in `Domain`, copied into every referencing host's output, and read after each host's `appsettings.json` and before environment variables, so a host cannot quietly disagree while a single container can still be overridden. `Domain/retention.json` (`RetentionSettings`) is the pattern to copy; `Domain/delivery.json` (`DeliverySettings`, the default `deliverTo` of an unprompted fire) is its second instance. When adding code that reads a new setting, update whichever category applies in the same change.

## Multi-Agent Patterns

- **Stuck workers**: replace, don't wait — spawn a fresh agent for the same task. Never retry the same failing action more than twice; after two failures reassess or escalate.
- **Layer completion**: check `TaskList` for `completed` on every task in a layer before starting dependent work or reporting success. Never infer completion from partial signals.
- **Auto-commit** after each TDD triplet (RED → GREEN → REVIEW) succeeds, with a message referencing the triplet's feature.

## Local Development

Compose files, the launch command, secrets mounts, and WebChat/Dashboard access live in the `launch-stack` skill (`.claude/skills/launch-stack/SKILL.md`). Home Assistant setup lives in `.claude/rules/home-assistant.md`.

## Agent skills

### Issue tracker

Issues and specs live as tracked markdown under `.scratch/<feature-slug>/` — not GitHub Issues, despite the remote. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical roles, each label string equal to its name, written as a `Status:` line in the issue file. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `docs/adr/` at the root, plus a `CONTEXT.md` created lazily when a term needs pinning down. See `docs/agents/domain.md`.
