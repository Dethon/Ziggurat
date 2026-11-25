# 01: Apps server and the `/apps` mount

Spec: `.scratch/app-spaces/spec.md` ("The apps server", "The `/apps` mount").

**What to build:** A household agent can build, version and delete a built app with the file tools it already has. A new shared server, the apps server, runs in the deployment's compose with a volume holding one git repository per built app. It serves the `apps` filesystem through the existing backend registrar and resource. Writing `/apps/<slug>/app.json` creates an app; the agent then writes its files. `./publish`, `./history` and `./rollback` version it, and removing the directory deletes it. The server is listed in the agent's shared servers. A new `apps` feature in the household's `features` section withholds it; the feature defaults off, and only the operator may set it (the toggle itself is ticket 11).

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [x] Each call acts for the call token's household and the call principal's person. A call naming nobody is refused.
- [x] `/apps` lists only the calling person's apps, one directory per slug. Another member's apps and another household's are never listed or reachable.
- [x] Writing `app.json` mints a six-character lowercase alphanumeric id, initialises the repository, and records the author and the building agent. A slug follows the existing slug rule and is unique per author.
- [x] The app manifest is validated whole (`name`, `shortName`, `accentColor`, `description`, `root`, `reads` as well-formed mount points, `connect` as bare `https` origins). Each refusal quotes the broken rule, and a refused file is never stored.
- [x] A turn of a registered (custom) agent cannot create an app, and the refusal says why: the turn's agent id (from `_meta`) must be in the household's agent-names view, as the scheduling server checks.
- [x] The mount offers glob, info, read, search, text create/edit, delete, move, copy, blob read/write and its own exec, by overriding only. No hand-written `fs_*` tool.
- [x] Writes under any `node_modules/` path are refused.
- [x] `./publish` commits the working tree if it changed, records that commit as the served one, and answers the app's link (`<slug>-<id>.<apps-domain>`).
- [x] `./history [--limit N]` lists commits newest first with the served one marked. `./rollback <rev>` makes an earlier commit the served one without rewriting history.
- [x] Bare action files run on the mount itself, so they work in a session with no sandbox.
- [x] `rm -r /apps/<slug>` deletes the repository and its metadata.
- [x] Quotas (apps per household, bytes per repository) are appsettings tunables. A refusal names the limit and its value.
- [x] With the `apps` feature off (the default), no definition is offered the server. With it on, the household's own agents get `/apps`.
- [x] Tests drive the server in process through its MCP tools, after the Sugary mount tests. The deployment conformance tests know the new service and volume.

## Comments

**Landed (branch `app-spaces`).** `McpServerApps` (container `mcp-apps`, port 6018 on host runs) serves the
`apps` filesystem (`McpServerApps/Vfs/AppsFileSystem.cs`) over a volume `./volumes/apps` chowned by
`mcp-apps-volume`: `<household>/<id>/` holds `app.meta.json`, `git/` and `tree/`. The manifest rules and the
id are in `Domain/Apps/`. The `apps` feature (`FeaturesSection.Apps`, `SharedServers.Apps`) defaults off; an
owner's features save strips and restores it (`HouseholdFeaturesAdmin`). The accounts service answers
`mcp-apps` the `agent-names` view on a call token. Contracts in `.claude/rules/apps.md`.
Tests: `McpAppsServerTests`, `AppsPublishingTests`, `AppManifestTests`, `BuiltAppIdTests`, plus rows in
`HouseholdAgentsTests`, `HouseholdFeaturesAdminTests`, `HouseholdConfigurationTests`, `McpServerRegistrations`,
`FileSystemServerConformanceTests`, `StatelessProtocolContractTests`, `DeploymentConformanceTests` and the smoke
tier's gated-server list (not run: the smoke tier was out of scope).

**Choices the spec left open:**
- *Which agents name the server:* the `jonas` template only (`servers:` line). The home's seeded agents are
  definition entries already saved in its `agents` section, so they reach `/apps` only once the operator adds
  `http://mcp-apps:8080/mcp` to their definitions; `HouseholdMigration/Home/agents.json` was left unchanged.
- *The git directory sits beside the working tree*, not in it (`git/` vs `tree/`): a writable `.git` would let a
  write to its hooks or config run code on the server. Any `.git` path segment is refused and never listed.
- *"Bytes per repository"* measures the whole app directory: working tree, history and metadata.
- *Manifest limits:* `name` 1–60, `shortName` 1–12, `description` 1–300; unknown fields are refused; `reads`
  entries are `/` plus a slug (an outpost's `outpost:<NAME>` is not accepted); `connect` entries must equal
  their own origin (`https://host[:port]`, no trailing slash). A slug is at most 40 characters so
  `<slug>-<id>-preview` stays one DNS label.
- *Moves* stay within one app (copy between apps instead); moving `/<slug>` to `/<new-slug>` renames the app
  and keeps its id. A whole app cannot be copied.
- *Reserved names* at an app's root: the three actions plus `check`, `errors` and `.checks`, so tickets 07
  and 08 can serve them without colliding with a file the agent wrote. They are not listed yet.
- *Quota refusals* use `permission_denied` (no new taxonomy code), the message naming the limit and value.
- *Publish* validates the manifest and that its `root` is a directory; the pinning check is ticket 03's.
- The apps domain is the compose literal `APPSDOMAIN: changeme` until the domain exists.
- Not updated: `.vscode/launch.json`, `.zed/debug.json` (IDE launchers); `scripts/run-local.sh` was. The eval
  stack does not host the apps server, so the template's endpoint is dropped there as unhosted.

