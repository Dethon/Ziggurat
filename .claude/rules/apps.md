---
paths:
  - "McpServerApps/**"
  - "Domain/Apps/**"
  - "Tests/Integration/Fixtures/AppsServer.cs"
  - "Tests/Integration/McpServerTests/*Apps*"
  - "Tests/Unit/Domain/Apps/**"
  - "Accounts/Admin/HouseholdFeaturesAdmin.cs"
---

# Built apps: the apps server and `/apps`

Spec: `.scratch/app-spaces/spec.md`. A **built app** is a web app an agent built, kept as a git
repository by the apps server (`McpServerApps`, container `mcp-apps`); plain "app" means an app behind
the proxy. Its **app manifest** is its `app.json`; its **preview** is its working tree.

## The server

- **A shared server, never in the tenant kit.** Listed in the agent's `sharedServers.apps`, named by the
  jonas template, and withheld from every definition by the household's **`apps` feature**
  (`FeaturesSection.Apps`), which is **off by default and the operator's alone**: an owner's features
  save strips `apps` from what it sent and from its `loaded`, then keeps the stored value
  (`HouseholdFeaturesAdmin`), because the server spends the deployment's shared storage.
- **Each call acts for the call token's person** (`Principal.Of(caller.Conversation)?.Account`, as
  Sugary's mount); their household is the account's tenant. A call naming nobody, or the guest, is
  refused `permission_denied` on every operation, never shown an empty list.
- **Only the household's own agents create or change a manifest**: the turn's `AgentId` must be in the
  household's `agent-names` view, read through `ICallHousehold.AgentNamesAsync` on the call token
  (`InternalCallers.Apps`, `CALLERS__mcp-apps`). No fallback to a registered catalogue: no household
  accessor is a refusal. A person's registered agent resolves for its owner alone, so nobody else could
  read through an app it built.
- **The volume** (`./volumes/apps`, chowned by `mcp-apps-volume`): `<household>/<id>/` holds
  `app.meta.json` (`BuiltApp`: id, slug, household, author, building agent, served commit), `git/` and
  `tree/`. **The git directory is never inside the working tree**: everything in the tree is the agent's
  to write, and a writable git directory runs code (hooks, config) on this server. Git runs through
  `AppRepository` only: arguments as a list, no system or global config, no hooks, no signing, a fixed
  identity, `--template=` empty, a deadline; a revision from the model is a hash prefix or `HEAD~N` and is
  passed after `--end-of-options`.
- **Ids** (`Domain/Apps/BuiltAppId`): six lowercase alphanumerics from a cryptographic generator, unique
  across every household (the hostname names none). The repository is named by id, never slug, so a
  renamed app keeps its links; the link is `https://<slug>-<id>.<AppsDomain>/`. `AppsDomain` is
  per deployment (compose `APPSDOMAIN`, a placeholder until the domain exists); quotas
  (`Quotas:AppsPerHousehold`, `Quotas:BytesPerRepository`, the whole app directory) are the server's
  appsettings. A refusal names the limit and its value.
- **Create, rename, delete, publish and rollback run one at a time per household**
  (`BuiltAppStore.OneAtATimeAsync`), so a quota or a slug is never decided on a listing another call is
  changing.

## The mount

- **One filesystem, `apps`**, declared by overriding (`AppsFileSystem`): glob, info, read, search, text
  create and edit, delete, move, copy, blob read and write, exec. Never a hand-written `fs_*` tool;
  `FileSystemServerConformanceTests` pins the set.
- **It lists the caller's own apps**, one directory per slug; another member's are not found, shared or
  not. A slug is the slug rule, at most `AppsFileSystem.SlugLength` (the preview label must stay one DNS
  label), unique per author.
- **Creating an app is writing `/<slug>/app.json`.** The manifest (`AppManifest.Parse`) is judged
  whole, every broken rule named in its own words, and **checked on every way in**: create, overwrite and
  edit (applied in memory with `TextEditTool.Apply`, stored only if it passes); a blob write, copy or move
  onto it is refused, and it cannot be deleted or moved away (removing the directory deletes the app).
  `reads` are checked for shape only (`/` + a slug); `connect` entries are bare `https` origins.
- **Text create takes any extension**, since a sandbox command's UTF-8 file arrives as a text create;
  the text read and search open the shared text list plus web extensions. Binary build output arrives
  through the ranged blob write a copy streams through.
- **Refused writes**: anything under a `node_modules/` segment (its own hint: build in the sandbox, copy
  the output), any `.git` segment (also hidden from listings), and the names the server keeps at an
  app's root — the action files, `check`, `errors`, `.checks` — for what later tickets produce.
- **Action files run on the mount** (a catalog, as `/timers`): `publish` (validates the manifest and its
  `root`, commits the working tree if it changed, records `HEAD` as served, answers the link), `history
  [--limit N]` (newest first, the served one marked `*`/`(served)`), `rollback <rev>` (moves the served
  pointer in the metadata only; history is never rewritten). Unknown is exit 127 naming the actions.
- Moving `/<slug>` to `/<new-slug>` renames the app; other moves stay within one app; copies may go
  between the caller's apps.

## Tests

`McpAppsServerTests` and `AppsPublishingTests` drive the server in process through its MCP tools
(`AppsServer`, after `SugaryServer`); `AppManifestTests`, `BuiltAppIdTests` the pure rules;
`HouseholdAgentsTests` and `HouseholdFeaturesAdminTests` the feature; the deployment and server tables
(`McpServerRegistrations`, `DeploymentConformanceTests`) know the service and its volume.
