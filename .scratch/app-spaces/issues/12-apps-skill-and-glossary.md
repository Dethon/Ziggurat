# 12: The `apps` skill and glossary terms

Spec: `.scratch/app-spaces/spec.md` ("The skill", "New domain terms").

**What to build:** The agent builds apps the same way every time. The apps server ships an `apps` skill, the way every skill is shipped by the server whose tools it teaches. It covers the app manifest, the client library, static versus npm builds (and that `node_modules` never enters `/apps`), CDN pinning with integrity, checks and assertion files, publish, history and rollback, and reading `errors`. `CONTEXT.md` gains the new terms: built app, app space, app manifest, preview, check, app sign-in.

**Blocked by:** 06, 07, 08

**Status:** ready-for-agent

- [x] The skill is listed only to agents offered `/apps`, and stays within the skills budget.
- [x] Every API, path and action file the skill names exists, checked by the skills' existing conformance tests.
- [x] Each `CONTEXT.md` entry carries its _Avoid_ line. Built app avoids plain "app", which already means apps behind the proxy; app sign-in avoids "handoff".

## Comments

**Landed (branch `app-spaces`).** `Domain/Prompts/Skills/apps.md` (handle `Domain/Prompts/AppsSkill.cs`), served by
the apps server (`.AddSkills(AppsSkill.Text)` in `McpServerApps/Modules/ConfigModule.cs`). Description 96 / 110
tokens, body 1,512 / 1,600 (snapshot `Tests/Snapshots/skill.apps.md`). It teaches what was built, not the spec's
first draft: the manifest (with a worked `app.json`), the page's content policy (no inline script but an import
map, `connect` for fetches), pinned CDN code (a worked `<script>` with a real `sha256-` hash, where to read a hash
on jsdelivr's data API, that a `.min.js` jsdelivr minifies on the fly has no listed hash, import maps), the client
library (`globalThis.apps`, `/_app/apps.mjs`, each member's result keys and options, that `read`'s content is
numbered and `blob` gives the file as it is, `ReadError` and its three codes), an npm build in the sandbox home
copied in by name (`node_modules` refused, `vfsChanges`, `root: dist`, a `.gitignore` not honoured), `./check`
(viewports, route, the PNGs and `report.json` keys, `checks/*.js` as async function bodies, exit 1, one per
household), publish, history, rollback, rename and delete (refused while an app space), an app space's approved
reach (what the publish answer means and that the preview is not held to it), limits, and the `errors` file's keys.
`CONTEXT.md` gains a "Built apps" section: built app, app manifest, preview, check, app space, approved reach, app
sign-in, each with _Avoid_. The apps rule names the skill (`.claude/rules/apps.md`, "The server").

**How the criteria are held:**
- *Listed only where `/apps` is:* the skill rides the apps server, which the `apps` feature withholds
  (`HouseholdAgentsTests.TheAppsServer_IsWithheldUntilTheAppsFeatureIsOn`); the staleness and contract tests pin it to
  `mcp-apps`; `AppsSkillTests.TheSkill_IsListedToAnAgentThatDialsTheAppsServer_AndToNoOther` assembles the jonas template
  (offered, 7,801 tokens against the 12,300 ceiling) and the home's jonas (not offered).
- *Every name exists:* `Tests/Unit/McpServerApps/AppsSkillTests.cs` extracts names from the words and checks each
  against the code: `./<action>` against `AppsFileSystem.Actions` (and every action is taught), `--flags` against the
  mount's exec description, `apps.<member>` against the module's exports (all taught), `/_app/…` against
  `AppOriginFiles`, each result's keys against the tool result's JSON names and each option against `AppRead`, the
  `ReadError` fields against the library and its codes against `ToolError.Codes`, the example manifest through
  `AppManifest.Parse` and its fields against `AppManifest.Fields`, the example script through `OutsideCodePinning`, the
  CDNs against the shipped `Cdns`, the check's output path and `report.json` keys against `AppChecks`/`ViewportReport`,
  and the errors file's keys and values against `AppErrorEntry`/`AppErrors.Kinds`. `PromptStalenessTests` walks the
  skill's paths (`/apps` joined its mount roots). To name them, `AppsFileSystem.Actions` and `AssertionsDirectory`,
  `AppManifest.Fields` and `AppErrorEntry.Published`/`Preview` became public constants (the last replacing literals in
  `AppErrorReports`).

**Red first:** the document alone was red in `PromptStalenessTests.Manifest_EverySkillDeclaration_…`,
`McpServerContractTests.EveryServer_ServesEverySkill…(apps)` and `ClaimCoverageTests.EveryDeclaredClaim_…` until the
server shipped it and the claim was exempted. `AppsSkillTests` passed on arrival (the words came first), so each case
was shown red by mutating the skill and restoring it: `./deploy`, `--max`, `apps.bytes`, `/_app/client.mjs`, a `data`
result key, a `rowLimit` option, a `code` field, an `unavailable` code, a `color` manifest field, a `label` field, the
`integrity` attribute dropped, a `cdnjs` CDN, `.checks/last/`, `failures`, a `person` errors key, and the skill dropped
from the fixture's served skills (the listing case) — each failed exactly its case.

**Choices and deviations:**
- *One claim, the trigger, exempted `Unwritten`*: the eval stack does not host the apps server, so no scenario can
  offer `/apps`. **No eval scenario for app building was added; it is a candidate for later** (it needs the apps
  server in the eval stack). `ChangeScopeTests`' skill-document theory now takes only skills a scenario tests: a skill
  with nothing citing it has no family to select.
- *No stub line in the jonas template or a prompt:* skills are advertised from their descriptions in the skills
  section, the mounts list carries `/apps`' own description, and the apps server serves no prompt. The description is
  the whole choosing rule.
- *Hashes without a sandbox* come from `https://data.jsdelivr.com/v1/packages/npm/<name>@<version>?structure=flat`
  (each file's `hash` is base64 SHA-256; checked against the bytes for chart.js 4.4.1 on 2026-11-25). The agent reads
  it with its web tools; nothing in this repo pins that external API.
- *No prompt snapshot changed*: no snapshotted agent dials `mcp-apps` (the home's seeded agents do not, ticket 01), so
  the standing figure is unchanged; the jonas template is measured in `AppsSkillTests` instead of a new snapshot.
- Jev's labelled preload set (`SkillPreloaderJevTests`, eval) has no app requests; not touched.

**Run:** `(~Apps|~Skill|~Prompt|~Snapshot|~Conformance|~ClaimCoverageTests|~ChangeScopeTests|~McpServerContractTests|
~TestAgentsTests)&FullyQualifiedName!~E2E`: 1,584 passed, 5 skipped (Jev's eval probes, two smoke cases). Not run: the
full suite, E2E, smoke, eval.

**Deploying this needs:** rebuild `base-sdk` first (Domain changed), then `mcp-apps` (it now serves the skill) and
`agent`. No new secret or setting.
