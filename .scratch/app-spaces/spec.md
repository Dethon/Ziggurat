# App spaces: the agent codes read-only web apps a household installs

Status: ready-for-agent
Branch: `app-spaces`, from `master`

## Problem Statement

A household's data reaches people in two ways today: by asking the agent in a chat, or through an
app someone hand-built for one purpose (Sugary, the KWGT widget). Wanting a glance at something —
this week's glucose beside the meals, which lights are on, what is printing, the media added this
month — means either a chat turn each time or a feature request that waits for a developer.

The agent can already read all of it: every mount of its virtual filesystem (`/sugary`, `/ha`,
`/timers`, `/schedules`, `/media`, `/vault`, `/print-queue`, `/watchers`) answers its file tools,
and `/sugary` answers SQL. It can write code too, and in a household with a sandbox it can run
`npm`. What it cannot do is turn that into something a person keeps on their home screen: there is
nowhere to put a web app, nothing to serve it, no way for a running page to read the household's
data as the person looking at it, and no way for the agent to see what it built before handing it
over.

## Solution

**The agent builds apps, and an app can be a space.** A person asks the agent for an app ("a page
with my glucose today and what I ate"). The agent writes a **built app** under a new mount,
`/apps`, served by a new shared server, the **apps server**, which a household's agents reach once
the operator has turned the `apps` feature on for it. Each built app is a git repository in the
apps server's volume.

**A built app is static files, however it was made.** In a household without a sandbox the agent
writes plain HTML, CSS and ES modules, with libraries pinned from a fixed list of CDNs by exact
version and integrity hash. In a household with a sandbox it may keep an npm project in the repo,
build it in the sandbox and copy the build output back into `/apps/<slug>/`. Either way the apps
server only ever serves files; it never runs household code.

**A built app reads as the person viewing it.** A served client library gives every built app the
same virtual paths the agent uses — `apps.read("/sugary/…")`, `apps.query("/sugary", "select …")`,
`apps.glob("/ha/light.*")` — answered by the agent's VFS through a read-only route, acting as the
signed-in viewer with exactly the reach their own chat turn would have. Two people opening the same
glucose app each see their own. A built app declares the mount points it reads, and only reads the
agent would run unasked are served. It may also fetch from the internet, straight from the viewer's
browser, to the origins it declares and no others.

**Each built app is its own site, on a domain of its own.** Built apps are served at
`<slug>-<id>.<apps-domain>`, a second registrable domain, so agent-written code never shares a site
with WebChat, the hub or the accounts service and no Ziggurat cookie ever reaches it. Opening one
signs the person in through an **app sign-in** redirected from the accounts service. Each has its
own manifest and service worker, so it installs as a PWA of its own.

**The agent sees what it built.** The apps server runs a headless Chromium. The agent runs
`./check` in the app's directory: the preview is loaded as the person whose turn ran it, at the
viewports asked for, and the screenshots, console messages, failed requests, policy violations, an
accessibility snapshot and the results of the app's own in-page assertions land in the mount, where
`file_read` shows the screenshots as images. Errors from real use land beside them, in
`/apps/<slug>/errors`.

**Draft, publish, share.** The agent edits the working tree; the **preview** origin shows the
working tree to the app's author alone. `./publish` commits and makes that commit what the app
serves; `./rollback <rev>` serves an earlier one. A published app is its author's: they can install
and use it. An owner can make it an **app space** from the spaces page, after which everyone in the
household finds it in WebChat's space switcher. A built app that is an app space cannot be deleted
until an owner removes the space.

## User Stories

### Asking for an app

1. As a household member, I want to ask the agent in a chat for an app that shows some of the
   household's data, so that I can see it at a glance instead of asking every time.
2. As a household member, I want the agent to tell me the app's link when it publishes it, so that I
   can open and install it right away.
3. As a household member, I want to ask the agent to change an app I already have ("add the last
   week", "make the chart bigger"), so that the app grows with what I need.
4. As a household member, I want to ask the agent to undo the last change to an app, so that a change
   I dislike does not stay.
5. As a household member, I want to ask the agent which apps I have, so that I can find one I made a
   while ago.
6. As a household member, I want to ask the agent to delete an app I no longer use, so that my list
   stays short.
7. As a household member, I want the agent to tell me why an app cannot be deleted when an owner has
   shared it, so that I know whom to ask.
8. As a household member, I want to ask the agent why an app is broken on my phone, so that it can read
   the app's errors and fix it.
9. As a household member in a household without the apps feature, I want the agent to say that it
   cannot build apps here, so that I am not promised something that will not appear.
10. As a household member talking to a registered (custom) agent, I want to be told that apps are
    built by the household's own agents, so that I ask the right one.

### Using an app

11. As a household member, I want to open one of my apps from WebChat's space switcher, so that I find
    my apps where I find my spaces.
12. As a household member, I want to install an app as its own PWA, with its own name, colour and icon,
    so that it sits on my home screen beside Sugary and WebChat.
13. As a household member, I want to be signed in to an app without a second passkey prompt when I am
    already signed in to Ziggurat, so that opening it is one tap.
14. As a household member, I want to be signed out of every app when my Ziggurat sessions end, so that
    an app on a lost phone stops showing my data.
15. As a household member, I want an app to show me my own data, never the author's, so that a shared
    glucose app shows each of us our own readings.
16. As a household member, I want an app to show a clear message where it reads something I may not
    see, so that I understand it is not broken.
17. As a household member, I want an app to say when a household server it reads from is offline, so
    that I know the data is missing rather than empty.
18. As a household member, I want an installed app that was deleted to say so when I open it, so that
    I do not keep a dead icon wondering.
19. As a household member, I want an app's shell to open quickly from the home screen, so that it
    feels like an app and not a website.
20. As a household member with an older WebChat still installed, I want my space switcher to keep
    working, so that an update elsewhere does not break the app I already have.

### Sharing apps in the household

21. As an owner, I want to make a member's published app an app space, so that everyone in the
    household can use it.
22. As an owner, I want to see, before sharing an app, who made it, which mounts it reads and which
    outside origins it talks to, so that I know what I am putting in front of the household.
23. As an owner, I want to stop sharing an app by removing its space, so that the household's list
    keeps only what is still useful.
24. As an owner, I want to order app spaces among chat spaces, so that the switcher reads the way the
    household uses it.
25. As a household member, I want a shared app to appear in my space switcher without doing anything,
    so that I hear about it by seeing it.
26. As a disabled member, I want app spaces to refuse me as WebChat does, so that being disabled means
    the same thing everywhere.
27. As a member of another household, I want never to be able to open, list or guess my way into this
    household's apps, so that households stay apart.

### The operator

28. As the operator, I want the apps feature off for every household until I turn it on, so that the
    shared browser and storage are spent only where I chose.
29. As the operator, I want per-household limits on apps, repository size and concurrent checks, so that
    one household cannot exhaust the deployment.
30. As the operator, I want a single wildcard DNS record, wildcard certificate and Caddy block for the
    apps domain, set up once, so that no new app ever needs me.
31. As the operator, I want the apps domain to be configuration, so that the smoke and E2E stacks run
    on local names and never on the deployment's domain.
32. As the operator, I want a household's errors from the apps server to stay visible to me, so that I
    can help a household whose app stopped working.

### The agent building an app

33. As the agent, I want an `/apps` mount listing the calling person's apps, so that I can find and
    change an app with the file tools I already use.
34. As the agent, I want creating an app to be writing its `app.json`, so that there is no new tool to
    learn.
35. As the agent, I want the app manifest refused with a precise reason when it is malformed, names an
    unknown mount or names an origin badly, so that I can fix it in one go.
36. As the agent, I want to write HTML, CSS and JS into the working tree with `create` and `edit`, so
    that static apps need no sandbox.
37. As the agent in a household with a sandbox, I want to build an npm project there and copy the build
    output — scripts, styles, fonts, images, wasm — into the app, so that I can use a bundler and
    frameworks.
38. As the agent, I want writes under `node_modules/` refused, so that I never ship a dependency tree
    into the repository by mistake.
39. As the agent, I want the served client library to take the same virtual paths my tools take and
    answer the same result shapes, so that what I learned with tools is how I write app code.
40. As the agent, I want a skill that teaches the app manifest, the client library, static versus npm
    builds, checks and publishing, so that I build apps the same way every time.
41. As the agent, I want `./check` to load the preview at the viewports I name and give me screenshots
    I can look at, in a household with or without a sandbox, so that I see the app before the person
    does.
42. As the agent, I want the check to report console errors, failed requests and content-policy
    violations, so that a missing allowlist entry or a typo shows up before publishing.
43. As the agent, I want the check to give me an accessibility snapshot, so that I can verify structure
    and text without reading pixels.
44. As the agent, I want to write in-page assertions in the app's repo and have the check run them in
    the page and report each one, so that I can test behaviour, not just looks.
45. As the agent, I want the check to read data as the person whose turn ran it, so that what I see is
    what they will see.
46. As the agent, I want `./publish` to refuse an app whose outside scripts are not pinned by exact
    version and integrity hash, so that what was checked is what runs.
47. As the agent, I want `./publish` to answer the app's link, so that I can hand it to the person.
48. As the agent, I want `./history` to list the app's commits with the published one marked, so that I
    can pick the commit to roll back to.
49. As the agent, I want `./rollback <rev>` to serve an earlier commit without losing later ones, so
    that a rollback can itself be undone.
50. As the agent, I want the app's runtime errors readable at `/apps/<slug>/errors`, so that a person's
    "it's broken" becomes a stack trace I can act on.
51. As the agent, I want deleting an app to be removing its directory, so that it works like cancelling
    a timer.
52. As the agent, I want a limit I hit (apps, size, concurrent checks) answered with the limit's name
    and value, so that I can tell the person instead of retrying.

### Security

53. As a household member, I want app code never to be able to send messages, approve tool calls or
    read conversations as me, so that a broken or injected app cannot act in my name.
54. As a household member, I want an app able to read only the mounts it declared, so that a glucose
    app cannot read my vault.
55. As a household member, I want an app never to write, move, delete or run anything, so that the
    worst an app can do is show me something.
56. As a household member, I want an app refused any read that would ask me for approval in a chat, so
    that an app never gets what the agent itself would have to ask for.
57. As a household member, I want an app to reach only the outside origins it declared, so that my data
    cannot be posted anywhere else.
58. As a household member, I want one app unable to read another app's storage or service worker, so
    that apps are isolated from each other as well as from Ziggurat.
59. As a household member, I want a private app reachable by its author alone, even with the link, so
    that "private" means private.

## Implementation Decisions

### The apps server

- A new **shared server**, `McpServerApps`, in the deployment's compose, never in the tenant kit. It
  answers each call for the household its verified call token names, and for the person the call's
  principal names, as the Sugary filesystem does. A call naming nobody is refused, so a turn acting
  for nobody (a fire, a download notice) has no apps and runs no check.
- It is listed in the agent's shared servers and withheld from every definition by a new **`apps`
  feature** in the household's `features` section. It defaults off, as Idealista does, and is the
  **operator's alone** to set, from the dashboard's household page; the owners' features page shows it
  and cannot change it.
- **Only the household's own agents build apps.** A registered (custom) agent resolves for its owner
  alone, so viewers could never read through it: a write of `app.json` from a turn whose agent is not
  one of the household's own is refused, naming why. The building agent is the turn's agent id the
  call's `_meta` carries, checked against the household's agent-names view as the scheduling server
  checks its agent directories.
- Storage is one volume holding a directory per household and a git repository per app, named by the
  app's id, never its slug. Repositories are operated through the git CLI in the image. Per-app
  metadata (id, slug, author, household, building agent, published commit, whether it is an app
  space) lives beside the repositories, not in them.
- Quotas are the server's own appsettings tunables: apps per household, bytes per repository,
  concurrent checks per household and in total, a check's deadline, and runtime errors kept per app.
  A refusal names the limit and its value.

### The `/apps` mount

- One filesystem, `apps`, through the existing backend registrar and resource; it declares its
  capability by overriding: glob, info, read, search, text create and edit, delete, move, copy and
  blob read/write (binary build output — fonts, images, wasm — arrives from a sandbox command as the
  blob write a copy streams through), and exec. No hand-written `fs_*` tool.
- **It answers its own exec**, as a catalog does (`/timers`, `/schedules`): its action files run on the
  mount, so `./check`, `./publish`, `./rollback` and `./history` work in a household without a sandbox;
  in one with a sandbox the bare action skips it, per the sandbox rule. Action files are served by the
  mount and never declared twice (ADR 0036); history is an action, as an entity's is (ADR 0037).
- It lists the calling person's own apps, one directory per app named by its **slug**. Another
  member's apps, shared or not, are not listed: an app is changed only by its author's turns.
- **Creating an app is writing `/apps/<slug>/app.json`.** The server mints the app's id (six lowercase
  alphanumerics), initialises the repository, records the author and the building agent, and counts it
  against the quota. A slug follows the existing slug rule and is unique per author.
- **The app manifest** (`app.json`, in the repository):
  - `name`, `shortName` (optional), `accentColor` (the space colour rule), `description`;
  - `root`: the directory served, default the repository root (an npm app names its build output,
    e.g. `dist`);
  - `reads`: the mount points the app may read (checked for shape here; the apps server cannot
    know an agent's mounts, so one the building agent lacks answers unmounted at the read API);
  - `connect`: outside origins the app may fetch, each an `https` origin, no wildcards, no paths.
  A write that breaks any rule is refused with the rule's words; the file is never stored half-valid.
- Server-produced, read-only, never in the repository:
  - `errors`: runtime errors, newest first;
  - `.checks/latest/`: the last check's `report.json` and one PNG per viewport.
- **Action files** in each app directory:
  - `check [--viewport WxH]... [--path <route>]` — runs a check against the preview (below);
  - `publish` — validates, commits the working tree if it changed, makes that commit the served one,
    answers the app's link;
  - `rollback <rev>` — makes an earlier commit the served one; history is never rewritten;
  - `history [--limit N]` — the commits, newest first, the served one marked.
- **Delete is removing the directory** (`rm -r /apps/<slug>` or the remove tool). It is refused while
  the app is an app space, naming the space; otherwise the repository and metadata go.
- Writes under any `node_modules/` path are refused.
- It is not a landing target and has no workspace; a sandbox sees it at `/apps` like every other
  served mount, which is how an npm build's output arrives.

### Serving built apps

- The apps domain is a per-deployment value (a compose environment entry); the E2E and smoke stacks
  use a local name. Caddy has one site block for `*.<apps-domain>` with a wildcard certificate through
  the existing Cloudflare DNS-01 snippet, sending everything to the apps server. Nothing else of the
  deployment is served on the apps domain, and the domain gets no `forward_auth`.
- A built app is **not an app behind the proxy**: app admission does not apply. The apps server
  decides who opens a built app.
- A hostname is `<slug>-<id>` for the published app and `<slug>-<id>-preview` for its working tree.
  The server parses the id from the label, so a renamed slug's old links still resolve. No household
  name or tenant id ever appears in a hostname.
- **Who may open what:** the published app — its author, and while it is an app space every member of
  its household who is not disabled; the preview — its author alone. Anyone else, and any unknown
  label, gets the same 404.
- Every response carries a content policy built from the manifest: `default-src 'none'`;
  `script-src 'self'` plus the fixed CDN list; `style-src` the same plus inline styles;
  `img-src 'self' data: blob:` plus the CDN list and `connect`; `font-src 'self'` plus the CDN list;
  `connect-src 'self'` plus `connect`; `frame-ancestors 'none'`; `base-uri 'none'`;
  `form-action 'none'`. The CDN list (jsdelivr, unpkg, esm.sh) is a setting of the apps server.
- The server injects into every served HTML document under the root the manifest link, the service
  worker registration and the client library. It serves a generated web manifest (the app's name, its
  colour, an icon drawn from the colour as the space icon is, `start_url` and `scope` `/`) and a
  generated service worker that caches the app's own files and never caches `/_vfs/*` answers.
- A deleted app's origin answers every navigation with a short "this app was removed" page, and its
  service worker with one that unregisters itself.
- `./publish` refuses an app whose HTML under its root loads a script or stylesheet from outside the
  app without an exact version in the URL and an `integrity` attribute, or whose import map names an
  outside module without an integrity entry.

### App sign-in

- An app origin keeps its own session: a host-only, `__Host-`-prefixed cookie, short-lived, naming an
  app session the apps server holds. The app session keeps the accounts session id (`sid`) it was
  made from.
- A navigation without one is redirected to a new route of the accounts service on the main host,
  carrying the app's origin. The accounts service checks the person's ordinary session there (signing
  in with a passkey if there is none) and redirects back with a single-use code bound to that origin.
  It accepts as a return target any origin under the configured apps domain, and nothing else.
- The apps server redeems the code service-to-service and receives a person assertion for a new
  `apps` channel, carrying `sid`. It renews assertions as the chat hub renews its connection's person,
  by the session refresh, never after the session is revoked.
- Every way sessions end (`SessionsEnded` on the accounts channel) ends that person's app sessions.
- The accounts service vouches for the person and household; whether that person may open that app is
  the apps server's decision.

### The app read API (the agent)

- A third adapter over the agent-side bridge's meaning layer (`VfsCall`), beside the file tools and
  the sandbox FUSE: an internal agent route answering `read`, `glob`, `info`, `search`, `query` and
  blob reads, with the tools' result shapes, on virtual paths. No operation that changes anything is
  reachable through it.
- The apps server is its only caller. It serves `/_vfs/*` on each app origin, authenticates the viewer
  by the app session, resolves the app, and forwards the read with **both** the agent API's secret
  (the caller is the apps server, as the agent registration API believes SexyTime) **and** the
  viewer's current `apps` assertion (who it acts for). The app's id, building agent and `reads` travel
  with the secret's authority; the person and household only with the assertion's. The agent believes
  no other header.
- The agent builds a session-less read registry for (household, building agent, viewer): the agent
  definition's servers, withheld by features and own-server state as a turn's are, each call signed as
  the viewer of that household. It is cached briefly and rebuilt when the household's servers change,
  as a turn's session is.
- A read is served only when its mount point is in `reads`, its operation is a read, and the building
  agent's whitelist would run it unasked; otherwise it answers the tools' permission envelope. The
  bridge's per-file byte ceiling applies.
- A building agent the household no longer has answers every read refused, naming it; an own server
  that is offline answers the tools' unavailable envelope.

### Checks

- The apps server runs headless Chromium in its own image. A check opens the preview as the person
  whose turn ran `./check`, with an app session the server makes itself from that call's verified
  principal (no sign-in redirect), loads each viewport and route, waits for the page to settle, and
  records one screenshot per viewport, console messages, failed requests, content-policy violations,
  an accessibility snapshot, and the outcome of each assertion file under `checks/` in the repo (JS
  run inside the page, each reporting pass or fail with a message).
- The browser reaches app origins at the apps server itself, never through public DNS; its data reads
  go through the same `/_vfs/*` path an installed app uses.
- Nothing of the app runs anywhere but inside the browser page.
- Checks beyond the quota are refused, not queued. A check past its deadline is stopped and its report
  says so.

### Runtime errors

- The client library reports uncaught errors, unhandled rejections and failed or refused reads to
  `/_errors` on the app's origin: message, stack, the virtual path and envelope for a read, the route,
  the user agent, the time and the viewer's person id. Never the data read.
- The apps server keeps the newest N per app, serves them as `/apps/<slug>/errors`, and logs them as
  household errors the operator sees.

### Spaces and WebChat

- `HouseholdSpace` gains an optional built app id. A space with one is an **app space**: its name and
  colour are the app's, it holds no topics, and `JoinSpace` on it answers "No such space."
- The spaces admin page lets an owner add an app space by picking one of the household's published
  apps, showing each one's author, `reads` and `connect` first. Removing the space unshares the app;
  it stays its author's. The admin page asks the apps server for the household's published apps.
- `GetSpaces` keeps answering chat spaces alone, for installed clients from before. A new hub method
  answers chat spaces, app spaces (with their URL) and the person's own published apps that are not
  app spaces, marked private; the switcher uses it, and picking an app opens its origin. The hub asks
  the apps server for the person's apps.

### The skill

- An `apps` skill shipped by the apps server, as every skill is shipped by the server whose tools it
  teaches: the manifest, the client library, static versus npm builds (and that `node_modules` never
  enters `/apps`), CDN pinning, checks and assertion files, publish, history and rollback, reading
  `errors`.

### New domain terms (for `CONTEXT.md`)

- **Built app**: a web app an agent built, kept as a repository by the apps server and served on an
  origin of its own. _Avoid_: app (alone — that is Sugary, WebChat, SexyTime, apps behind the proxy),
  site, widget.
- **App space**: a household space whose page is a built app.
- **App manifest**: a built app's `app.json`.
- **Preview**: a built app's working tree, served to its author alone.
- **Check**: a run of a built app's preview in the apps server's browser, reported into the mount.
- **App sign-in**: the accounts service's redirect that gives a built app's origin a session.
  _Avoid_: handoff (web search's sign-in handoff is another thing).

## Testing Decisions

A good test here drives a service through the surface its callers use — MCP tool calls and HTTP
requests against a real in-process host, a real browser for the client — and asserts what a caller
sees: listings, envelopes, response headers, served bytes, reports. Never an internal type's state.
Every test talks to `FakeOpenRouter`; no tier spends money.

Four seams, highest first:

1. **The apps server, in process**, through its MCP `/apps` mount and its HTTP host addressed by Host
   header: create by manifest, manifest refusals, custom-agent refusal, preview versus published,
   publish, history and rollback, binary copy, CSP and injected manifest/service worker, the pinning
   refusal, who may open what (author, app space, other member, other household, disabled member),
   deleted app page, delete refused while an app space, quotas, the `node_modules` refusal, the errors
   route and file, and checks with real Chromium (screenshot present, console error reported, CSP
   violation reported, assertion pass and fail). Prior art: the Sugary API and mount integration tests
   (`Sugary*ApiTests`, `SugaryGlucoseMountTests`, `McpSugaryServerTests`).
2. **The agent's app read API over the wire**, as a viewer, against in-process MCP servers: two viewers
   get their own `/sugary` rows; an undeclared mount, a write and an approval-gated read are refused
   with the tools' envelope; a withheld feature or offline own server answers as a turn would; a
   request without the secret, or without a believable assertion, is refused. Prior art:
   `VfsBridgeOverTheWireTests`.
3. **The app sign-in**, as accounts integration: a code is issued only to an origin under the apps
   domain, is single-use and bound to that origin, redeems to an `apps` assertion carrying `sid`, and
   an ended session ends the app session on the apps server. Prior art: the accounts integration tests.
4. **One E2E collection** on the compose stack, apps origin on a local wildcard name: a signed-in member
   sees an app space in the switcher, opens it, is signed in through the app sign-in, and the app
   renders data read through `/_vfs/*`; a second household's member gets a 404. Its own collection, per
   the E2E rules.

Conformance: the deployment and Caddy conformance tests gain the apps server and the apps domain block;
the hub keeps `GetSpaces`' old answer; `PaidCallsConformanceTests` covers the new tests unchanged.

## Out of Scope

- Built apps that write, run action files or call a model.
- Live updates pushed to apps; apps poll.
- A server-side fetch proxy; outside data the browser cannot fetch (no CORS) is out of reach.
- Building on the apps server; npm builds happen in a household's own sandbox or not at all.
- Members changing each other's apps, or several authors on one app.
- Built apps from registered (custom) agents.
- Sharing apps across households, custom domains per app, public (signed-out) apps.
- Apps reached from Telegram or voice beyond the agent sending the link.
- Offline data in apps; the service worker caches the app's files only.

## Further Notes

- Operator set-up, once: buy the apps domain, add it as a Cloudflare zone, give the existing DNS token
  edit rights on it, add one wildcard record, set the apps domain in the deployment's environment.
- The session cookie's domain covers every herfluffness.com subdomain, which is why built apps live on
  a second registrable domain rather than a subdomain: no hub endpoint needs to defend against app
  code.
- Today no VFS read happens outside an agent session; the read registry for a viewer without a
  conversation is the one genuinely new piece of agent plumbing, and seam 2 pins it.
- The building agent is recorded on the app rather than resolved from the space's default agent, so an
  app keeps reading the same mounts however it is shared.
