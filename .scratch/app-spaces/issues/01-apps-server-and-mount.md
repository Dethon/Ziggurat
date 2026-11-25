# 01: Apps server and the `/apps` mount

Spec: `.scratch/app-spaces/spec.md` ("The apps server", "The `/apps` mount").

**What to build:** A household agent can build, version and delete a built app with the file tools it already has. A new shared server, the apps server, runs in the deployment's compose with a volume holding one git repository per built app. It serves the `apps` filesystem through the existing backend registrar and resource. Writing `/apps/<slug>/app.json` creates an app; the agent then writes its files. `./publish`, `./history` and `./rollback` version it, and removing the directory deletes it. The server is listed in the agent's shared servers. A new `apps` feature in the household's `features` section withholds it; the feature defaults off, and only the operator may set it (the toggle itself is ticket 11).

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Each call acts for the call token's household and the call principal's person. A call naming nobody is refused.
- [ ] `/apps` lists only the calling person's apps, one directory per slug. Another member's apps and another household's are never listed or reachable.
- [ ] Writing `app.json` mints a six-character lowercase alphanumeric id, initialises the repository, and records the author and the building agent. A slug follows the existing slug rule and is unique per author.
- [ ] The app manifest is validated whole (`name`, `shortName`, `accentColor`, `description`, `root`, `reads` as well-formed mount points, `connect` as bare `https` origins). Each refusal quotes the broken rule, and a refused file is never stored.
- [ ] A turn of a registered (custom) agent cannot create an app, and the refusal says why: the turn's agent id (from `_meta`) must be in the household's agent-names view, as the scheduling server checks.
- [ ] The mount offers glob, info, read, search, text create/edit, delete, move, copy, blob read/write and its own exec, by overriding only. No hand-written `fs_*` tool.
- [ ] Writes under any `node_modules/` path are refused.
- [ ] `./publish` commits the working tree if it changed, records that commit as the served one, and answers the app's link (`<slug>-<id>.<apps-domain>`).
- [ ] `./history [--limit N]` lists commits newest first with the served one marked. `./rollback <rev>` makes an earlier commit the served one without rewriting history.
- [ ] Bare action files run on the mount itself, so they work in a session with no sandbox.
- [ ] `rm -r /apps/<slug>` deletes the repository and its metadata.
- [ ] Quotas (apps per household, bytes per repository) are appsettings tunables. A refusal names the limit and its value.
- [ ] With the `apps` feature off (the default), no definition is offered the server. With it on, the household's own agents get `/apps`.
- [ ] Tests drive the server in process through its MCP tools, after the Sugary mount tests. The deployment conformance tests know the new service and volume.
