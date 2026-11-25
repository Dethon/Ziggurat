# 08: Runtime errors

Spec: `.scratch/app-spaces/spec.md` ("Runtime errors").

**What to build:** When a person says an app is broken on their phone, the agent can see why. The client library reports uncaught errors, unhandled rejections, and failed or refused reads to `/_errors` on the app's origin. The apps server keeps the newest N per app, serves them read-only at `/apps/<slug>/errors` newest first, and logs them as household errors the operator sees.

**Blocked by:** 06

**Status:** ready-for-agent

- [x] Each entry carries message, stack, route, user agent, time and the viewer's person id. A read failure also carries its virtual path and envelope. No entry ever carries data that was read.
- [x] Only a signed-in viewer of that app can report, and entries land on that app alone.
- [x] The kept count is an appsettings tunable, and older entries drop.
- [x] The errors are visible on the operator's side as household errors.

## Comments

**Landed (branch `app-spaces`).** The wire and the report's shape are `Domain/Apps/AppErrors`
(`AppErrors.OriginPath` = `/_errors`, `AppErrorReport.Parse`, `AppErrorEntry`). Apps server:
`AppPages.ReportAsync` (origin, viewer, app), `Serving/AppErrorReports` (shape, per-session minute, stamping,
the operator's event), `Store/AppErrorLog` (`<app>/errors`, newest `Errors:Kept`), the mount's
`/<slug>/errors`. Client: `AppOriginFiles.Reader` installs the reporting with the library. Contracts are in
`.claude/rules/apps.md` ("Runtime errors"), with a line in `observability.md` ("Households").
Tests:
- `AppsRuntimeErrorsTests` (23, by Host in process):
  - kept and stamped by the server;
  - the one 404 for nobody, another member and another app's session;
  - a foreign origin's page;
  - 11 shapes refused, the three caps either side;
  - a read's envelope, with retryability re-derived;
  - `Errors:Kept`;
  - the per-session minute on a fake clock;
  - the mount's file: listed, read, refused to create, edit, delete and move, never served, not counted;
  - the operator's `ErrorEvent` under the household;
  - a check's report, answered but kept nowhere and outside the minute;
  - text kept unescaped.
- A Chromium case in `AppsServingInABrowserTests`: a published app throwing fifty times, a rejection, a
  non-Error rejection holding "her lentils", and a caught and an uncaught failed read. That gives exactly
  five entries, no data, no policy violation.
- An assertion in `AppChecksTests`: a check's throw is kept nowhere but the check's report, and `/_errors`
  never shows as a failed request.
- `AppsReadsTests.AReadFromAnotherOriginsPage_IsTheOne404` (below).
- An `mcp-apps` row in `MetricsRegistrationContractTests`.

**Red first:** the endpoint, cross-origin, shape, caps, read envelope, rate-limit, quota (`SizeOf` counted
the file), operator, check-tag, registration-contract and Chromium cases were each seen failing before the
code. Two cases passed on arrival and were proven by breaking the code: dropping `Take(Kept)` failed the
retention case, and dropping the author check failed the 404 case. The mount's write refusals rode ticket
01's reserved name and passed on arrival; taking `errors` out of the reserved names failed that case. The
check-session, unescaped-text and foreign-origin-read cases were red first too.

**Choices the spec left open:**
- *Path:* the spec's `/_errors`, kept. It is a POST, dispatched before anything else, and app files are
  served on GET alone, so no file can shadow it. The worker already passed it by, so the spec needed no
  change.
- *A page of this origin alone:* every app shares the apps domain, so a sibling app's page is same-site.
  Its POST would carry this origin's `__Host-` cookie, which is host-only but still sent same-site. A report
  is therefore taken only when the `Origin` header's authority is the Host; anything else gets the one 404.
  This was not tested in a browser; the in-process case sends a foreign `Origin`.
- *Server-stamped fields:* time (the server's clock), user agent (the header, cut at 500), viewer (the
  session's user id; the household is the app's), `origin` (published or preview). The page sends
  only `kind`, `message`, `stack`, `route` and, for a read, `read {operation, path, envelope {errorCode,
  message, hint}}`. The envelope's `retryable` is derived from the taxonomy here, never taken from the page.
- *Shape:* strict deserialization (unknown members refused), a 16 KiB body, message 1 to 1000, stack up to
  8000, route a path (no query or fragment) up to 1000, a known kind, a known error code, a read operation,
  and `read` present for a read and only for a read. Any of these broken is a 400.
- *Never data that was read:* the shape has nowhere to put a row. The library describes a thrown or rejected
  non-Error by its type alone, never by its value, and reports a read as its operation, path and envelope.
  **Not enforceable:** an Error's own message is sent, bounded only by the caps, and the server cannot tell
  one carrying data from any other. That covers an app writing what it read into an error, and also an
  engine message quoting its input: Chromium's `JSON.parse` names the text it could not parse. The second was
  reasoned from Chromium's message format, not reproduced here.
- *Limits:*
  - Client: the same report once per page, at most ten a minute.
  - Server: `Errors:PerSessionPerMinute` (30), a fixed minute counted from a session's first report, then
    429 and dropped.
  - `Errors:Kept` (100) per app.
  - Both ship in `appsettings.json` alone; they are generic tunables.
- *Format:* one JSON object a line, unescaped (the relaxed encoder: the agent reads it as text and no browser
  is served it), newest first by arrival (never sorted on time), rewritten whole one
  report at a time per app. Stored at `<app>/errors`, extensionless, so the text read opens it through the
  shared list's `""` entry. A report for an app deleted meanwhile is dropped without making its directory
  again.
- *Checks:* a check's page reports through its own session too. It is answered 204 and kept nowhere, before
  its minute is counted. `report.json` already shows the agent those console errors, and no person met them.
  Kept, every viewport of every check would be a new page, so its reports would push people's out of the
  newest `Errors:Kept`, and four viewports could run past the per-session minute into the check's
  `failedRequests`. So each real breakage is counted once, in one place.
- *The operator's side:* `AppRuntimeError` `ErrorEvent`, `Service` `mcp-apps`, message
  `<slug>-<id>[-preview]: <kind>: <message>` and never the stack, under `ForHousehold(app.HouseholdId)`.
  This needed the apps server to become a metrics publisher, `AddMetricsPublishing("mcp-apps")`, inside its
  existing Redis block, so a host run or test without Redis publishes nothing. Side effects:
  - It now beats a heartbeat, so `mcp-apps` left Observability's `HttpProbes`.
  - The shared `CallToolErrorFilter`'s failed tool calls, and `CallTokenGate`'s refusals, on this server now
    reach the feed too, as the rule for every publishing host says.
- *Refactor on the way:* `AppPages.BodyAsync` (the capped body read) is shared by reads and reports.
  `AppPages.MayOpen` is the one "who may open this app" rule that pages, reads and reports ask, so ticket
  09's members widen all three at once.
  `AppOriginFiles.Library`/`Module` became `static readonly`, so the library's caps and path are interpolated
  from Domain rather than retyped.

**Fixed on the way:** `/_vfs/*` had the same same-site exposure. A sibling app's page could make a viewer's
browser POST a read to another app's origin with that origin's cookie. The response was unreadable
cross-origin and a read changes nothing, but it was still a read spent as that viewer. Reads now pass the
same `Origin` check, answering the one 404 otherwise (`AppsReadsTests`). The fixture's `ReadAsync` sends its
page's `Origin`, as a browser does. A check's reads and the Chromium read case still pass.

**Not verified:** the smoke tier, E2E, the eval and the full suite were not run. The `mcp-apps` image was not
built. No dashboard was opened to see an `AppRuntimeError` row: the event was pinned in process with a
recording publisher, and the collector's handling of `ErrorEvent`s is existing, tested code.

**Deploying this needs:** no new secret or compose value (`Errors` is appsettings; Redis was already wired).
Rebuild `base-sdk` first, since Domain changed. Then rebuild `mcp-apps` and `observability` (the probe list
changed). Until the apps server runs this build its pages' library sends nothing; after it, an open page
keeps the old library until it reloads.

**Follow-up: a member's errors are no data channel (branch `app-spaces`).** The page wrote every word of a
report, and every member's landed in the author's `/<slug>/errors` (and the operator's event repeated the
message), so a shared app could `throw new Error(JSON.stringify(rows))` and hand each viewer's data to its
author past the owner-approved `connect` reach. Now only the author's own report keeps its text. Another
member's is kept reduced (`Domain/Apps/AppErrorReduction`, applied in `AppErrorReports.ReducedAsync` against
what that origin serves, `AppPages.ServedFiles.AtAsync`):
- `kind`; `name`, the message's prefix when it is a standard ECMAScript error type, else `Error`;
- `frames`: the stack's first ten places on the report's own Host whose path is a served file (or one of
  `/_app/`'s), as `/path:line:column`, the query dropped;
- `route` only when it names a served file or directory;
- for a read: the operation, the path cut to its mount point only when the origin's manifest reads it, and
  the envelope's code;
- `reduced`, a note telling the agent to ask the person or run `./check`.

No message, stack, user agent, envelope message or hint. The operator's event is `<label>: <kind>: <name>`
for a member's report. *Deviation from the brief:* it kept the route path without its query. The path is
the page's too (`history.pushState('/' + rows)`), a 1000-character channel, so it is kept only when it names
something the origin serves. The User-Agent was dropped too: Firefox lets a page set it on `fetch`. What
remains is a numeric channel (which frames, their lines and columns, the kind, type and served route),
bounded by ten frames and `Errors:PerSessionPerMinute`.
Test: `AppsRuntimeErrorsTests.AnotherMembersReport_KeepsOnlyWhatTheServerChecks_AndNoTextThePageWrote`
plants one string in every field a page controls (message, stack names, paths and query, route, read path,
envelope message and hint, User-Agent), posts it as a member of a shared app and finds it nowhere in the file
or the operator's events; the author's own same report keeps it. It was red first.

