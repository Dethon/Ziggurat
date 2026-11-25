---
name: apps
description: Built apps under `/apps` — a page or home-screen app showing the household's data ("an app with my glucose today and what I ate"): making one, changing, checking, publishing, rolling back, renaming or deleting it, or finding why it broke. The app.json manifest, the client library, static and npm builds, pinned CDN code, ./check, ./publish, ./history, ./rollback and the errors file.
description-budget: 110
# Everything an app takes, from the manifest to its errors, paid once by a conversation that builds
# one: the apps server serves no prompt section, so nothing about building an app is standing.
body-budget: 1600
served-by: mcp-apps
claims:
# The trigger claim: a request of this kind loads the skill. No scenario cites it yet: the eval stack
# does not host the apps server (ClaimExemptions).
- apps.loads-for-an-app-request: A request to build, change, check, publish, roll back, rename, delete or debug a built app loads the apps skill before /apps is touched.
---

A built app is static files in a git repository at `/apps/<slug>/`, served on an origin of its own. It reads the household as **whoever is viewing it**, never as you, and it writes nothing.

### Making one

1. `domain__filesystem__text_create` at `/apps/<slug>/app.json` creates the app (a slug is lowercase letters, digits and hyphens, at most 40). A refusal names every broken rule; fix them all in one write.

   ```json
   {
     "name": "Glucose today",
     "shortName": "Glucose",
     "accentColor": "#2f7d6d",
     "description": "Today's glucose beside what I ate.",
     "reads": ["/sugary"],
     "connect": ["https://api.open-meteo.com"]
   }
   ```

   `reads` are the mount points the page may read; `connect` the bare https origins it may fetch, and no others. `root` is the served directory (default the repository root; `dist` for a build). `shortName` (12 at most) labels the home-screen icon.
2. Write the pages under the root, `index.html` the entry: HTML, CSS and ES modules. The server adds the web manifest, icon, service worker and client library to every page.
3. Check, look, fix, check again.
4. `./publish` and give the person the link it answers.

### The page's limits

Scripts come from the app and three CDNs only: no inline script but an import map, no `onclick=` attributes; inline styles are fine. `fetch` and images reach the app's own origin, `data:`, `blob:` and the `connect` origins.

Outside code is loaded from `https://cdn.jsdelivr.net`, `https://unpkg.com` or `https://esm.sh`, with an exact version in the URL and an `integrity` hash, or `./publish` refuses it:

```html
<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.js" integrity="sha256-dEAdc43T4D7l37O2hBIQ/ixOrYqWDEARykugt4qf2PM=" crossorigin="anonymous"></script>
```

The hash is `sha256-` plus the file's `hash` in `https://data.jsdelivr.com/v1/packages/npm/<name>@<version>?structure=flat`; pin a file that list names (a `.min.js` it lacks is minified on the fly, with no listed hash). In a sandbox, hash the exact bytes instead. An import map gives every outside module its own `integrity` entry and maps no prefixes; imports inside a module are not checked, so map them. Fewer libraries is better: a chart is a few lines of SVG.

### Reading the household

Every page has `apps` as a global (a module may `import apps from '/_app/apps.mjs'`; bundled code uses `globalThis.apps`). Each call takes the virtual paths your file tools take and answers their result:

- `apps.query('/sugary', sql, {maxRows})` → `{columns, rows, rowCount, truncated}`, each row an array in column order.
- `apps.read(filePath, {offset, limit})` → `{content, totalLines, truncated}`; `content` is numbered (`1: …`), as your reads are.
- `apps.blob(path)` → a `Blob` of the file as it is: `JSON.parse(await (await apps.blob(path)).text())` reads a JSON file.
- `apps.glob(basePath, pattern)` → `{entries, truncated}`; `apps.info(path)`; `apps.search(query, {directoryPath})` → `{results, totalMatches}`.

A refused read throws `apps.ReadError`, carrying `errorCode`, `message`, `hint` and `path`. Show it in the page as a sentence: `permission_denied` is a read this viewer or this app is not granted, `transient_dependency` a household server offline, `authentication` a lost sign-in (reloading signs in again). Only reads you would make unasked are served; one needing approval is always refused.

### An npm build (a household with a sandbox)

Keep the project in the sandbox home (`~/apps/<slug>`) and install and build there: `node_modules` is refused under `/apps`. Copy the source and the build in by name (`cp -r package.json package-lock.json src dist /apps/<slug>/`), read `vfsChanges`, and set `"root": "dist"`. Publishing commits everything written, whatever a `.gitignore` says. Without a sandbox, write static files.

### Checking

`domain__filesystem__exec` `./check` in `/apps/<slug>` (`--viewport 390x844`, up to four; `--path /route`) opens the preview, the working tree, in a browser as the person whose turn this is. Then read `/apps/<slug>/.checks/latest/`:

- `<W>x<H>.png` with `domain__filesystem__file_read`: what the page looks like.
- `report.json`, per viewport: `console`, `failedRequests`, `policyViolations` (a fetch to an origin missing from connect shows here), `accessibility` (the page's text and structure) and `assertions`.

Assertions are `checks/*.js` in the app, each the body of an async function run in the page: returning passes (the value is the message), throwing fails.

```js
const meals = document.querySelectorAll('#meals li');
if (meals.length === 0) throw new Error('no meals listed');
return `${meals.length} meals`;
```

Exit 1 means an assertion failed or the check did not finish. One check runs at a time per household.

### Publishing and after

- `./publish` commits the working tree and serves it, answering the link. It refuses an invalid manifest, a missing root and unpinned outside code, naming each.
- An app the household shares as an **app space** is granted only the reads and origins an owner approved; the preview is not held to that. When the publish answer names what waits on an owner, tell the person: an owner approves it on the spaces page.
- `./history [--limit N]` lists commits newest first, the served one marked; `./rollback <rev>` (a hash or `HEAD~1`) serves an earlier one, history kept.
- Rename by moving `/apps/<slug>` to `/apps/<new-slug>`; old links keep working. Delete with `domain__filesystem__remove` on `/apps/<slug>`, refused while it is an app space: an owner removes the space first.
- A limit (apps, bytes, checks) is refused naming it and its value: tell the person rather than retrying.

### When it breaks

`/apps/<slug>/errors`, once a page has reported one, holds what viewers' browsers met, newest first, a JSON object a line: `time`, `kind` (`error`, `rejection`, `read`), `message`, `stack`, `route`, `read` (a failed read's path and envelope), `viewer` and `origin` (`published` or `preview`). Read it, fix the cause, check, publish.
