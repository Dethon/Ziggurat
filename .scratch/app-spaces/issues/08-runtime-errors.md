# 08: Runtime errors

Spec: `.scratch/app-spaces/spec.md` ("Runtime errors").

**What to build:** When a person says an app is broken on their phone, the agent can see why. The client library reports uncaught errors, unhandled rejections, and failed or refused reads to `/_errors` on the app's origin. The apps server keeps the newest N per app, serves them read-only at `/apps/<slug>/errors` newest first, and logs them as household errors the operator sees.

**Blocked by:** 06

**Status:** done

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
  - a check's report.
- A Chromium case in `AppsServingInABrowserTests`: a published app throwing fifty times, a rejection, a
  non-Error rejection holding "her lentils", and a caught and an uncaught failed read. That gives exactly
  five entries, no data, no policy violation.
- An assertion in `AppChecksTests` (a check's throw lands marked `check: true`, and `/_errors` never shows
  as a failed request).
- An `mcp-apps` row in `MetricsRegistrationContractTests`.

**Red first:** the endpoint, cross-origin, shape, caps, read envelope, rate-limit, quota (`SizeOf` counted
the file), operator, check-tag, registration-contract and Chromium cases were each seen failing before the
code. Two cases passed on arrival and were proven by breaking the code: dropping `Take(Kept)` failed the
retention case, and dropping the author check failed the 404 case. The `AppChecksTests` assertion was proven
by making the server refuse a check's session. The mount's write refusals rode ticket 01's reserved name and
passed on arrival; they were not broken on purpose.

**Choices the spec left open:**
- *Path:* the spec's `/_errors`, kept. It is a POST, dispatched before anything else, and app files are
  served on GET alone, so no file can shadow it. The worker already passed it by, so the spec needed no
  change.
- *A page of this origin alone:* every app shares the apps domain, so a sibling app's page is same-site.
  Its POST would carry this origin's `__Host-` cookie, which is host-only but still sent same-site. A report
  is therefore taken only when the `Origin` header's authority is the Host; anything else gets the one 404.
  This was not tested in a browser; the in-process case sends a foreign `Origin`.
- *Server-stamped fields:* time (the server's clock), user agent (the header, cut at 500), viewer (the
  session's user id; the household is the app's), `origin` (published or preview), `check`. The page sends
  only `kind`, `message`, `stack`, `route` and, for a read, `read {operation, path, envelope {errorCode,
  message, hint}}`. The envelope's `retryable` is derived from the taxonomy here, never taken from the page.
- *Shape:* strict deserialization (unknown members refused), a 16 KiB body, message 1 to 1000, stack up to
  8000, route a path (no query or fragment) up to 1000, a known kind, a known error code, a read operation,
  and `read` present for a read and only for a read. Any of these broken is a 400.
- *Never data that was read:* the shape has nowhere to put a row. The library describes a thrown or rejected
  non-Error by its type alone, never by its value, and reports a read as its operation, path and envelope.
  **Not enforceable:** an app that writes what it read into an Error's own message sends it. That message is
  bounded only by the caps, and the server cannot tell it apart from any other message.
- *Limits:*
  - Client: the same report once per page, at most ten a minute.
  - Server: `Errors:PerSessionPerMinute` (30), a fixed minute counted from a session's first report, then
    429 and dropped.
  - `Errors:Kept` (100) per app.
  - All appsettings only; they are generic tunables.
- *Format:* one JSON object a line, newest first by arrival (never sorted on time), rewritten whole one
  report at a time per app. Stored at `<app>/errors`, extensionless, so the text read opens it through the
  shared list's `""` entry. A report for an app deleted meanwhile is dropped without making its directory
  again.
- *Checks:* a check's page reports through its own session too. The report is kept with `check: true` and
  sent to the operator only for a person's session. `report.json` already shows the agent those console
  errors, and no person met them, so the Errors page counts each real breakage once.
- *The operator's side:* `AppRuntimeError` `ErrorEvent`, `Service` `mcp-apps`, message
  `<slug>-<id>[-preview]: <kind>: <message>` and never the stack, under `ForHousehold(app.HouseholdId)`.
  This needed the apps server to become a metrics publisher, `AddMetricsPublishing("mcp-apps")`, inside its
  existing Redis block, so a host run or test without Redis publishes nothing. Side effects:
  - It now beats a heartbeat, so `mcp-apps` left Observability's `HttpProbes`.
  - The shared `CallToolErrorFilter`'s failed tool calls, and `CallTokenGate`'s refusals, on this server now
    reach the feed too, as the rule for every publishing host says.
- *Refactor on the way:* `AppPages.BodyAsync` (the capped body read) is shared by reads and reports.
  `AppOriginFiles.Library`/`Module` became `static readonly`, so the library's caps and path are interpolated
  from Domain rather than retyped.

**Seen, not fixed (outside this ticket):** `/_vfs/*` has the same same-site exposure. A sibling app's page
can make a viewer's browser POST a read to another app's origin with that origin's cookie. The response is
unreadable cross-origin and a read changes nothing, so it was left alone. The same `Origin` check would
close it.

**Not verified:** the smoke tier, E2E, the eval and the full suite were not run. The `mcp-apps` image was not
built. No dashboard was opened to see an `AppRuntimeError` row: the event was pinned in process with a
recording publisher, and the collector's handling of `ErrorEvent`s is existing, tested code.

**Deploying this needs:** no new secret or compose value (`Errors` is appsettings; Redis was already wired).
Rebuild `base-sdk` first, since Domain changed. Then rebuild `mcp-apps` and `observability` (the probe list
changed). Until the apps server runs this build its pages' library sends nothing; after it, an open page
keeps the old library until it reloads.
