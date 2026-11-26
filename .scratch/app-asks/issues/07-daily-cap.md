# 07: The daily cap

Spec: `.scratch/app-asks/spec.md` ("The cap").

**What to build:** Asks are counted per household and app over a rolling 24 hours, when the turn is
sent. Over the limit, an ask is refused with the cap code, which names the limit. Counts survive an
apps-server restart. The limit for now is the deployment's default, an apps-server setting.

**Blocked by:** 02

**Status:** done

- [x] The Nth ask within 24 hours succeeds and the next is refused with the cap code naming the limit (origin seam, fake clock).
- [x] An ask refused before sending counts nothing.
- [x] Every viewer of a shared app shares one count. Another household's copy of the app, or another app, has its own.
- [x] The window rolls: asks older than 24 hours stop counting.
- [x] Counts are durable on the apps server's volume and survive a restart.

## Comments

**Done (2026-11-26).** What 08-11 build on:

- **The refusal**: **429 `quota_exceeded`**, a new `ToolError.Codes` entry, **not retryable** (the cap frees as
  the day rolls on, so asking again sooner changes nothing; its default recovery says so). The apps server's
  other quotas answer `permission_denied`, which `/_ask` already uses for five other refusals, so the page
  could not have told the cap apart. `AppAsks.Capped(limit, freesAt)`: the message names the limit ("daily
  limit of N asks, counted for the whole household over the last 24 hours"); the envelope's **`freesAt`**
  (`AppAsks.FreesAtField`, ISO 8601) is when an ask is next counted, the hint says it in UTC, and both are
  absent for a cap of 0 (nothing ever frees). It is the exact moment enough of the oldest minutes roll out to
  come under the limit, so a lowered cap is answered right too. `apps.AskError.envelope` already carries the
  field; the client library is unchanged.
- **When it counts**: after the body, the instructions and a check's session are refused, **before the
  conversation is looked at** (so an ask past the cap opens no conversation), and **given back** if the
  conversation is refused (410) or `TurnReply.NotListening` (never delivered: an agent outage with a page
  retrying would otherwise burn the cap). A timeout, a failed turn or the page giving up still count: the turn
  was sent. A check's ask is refused before counting today (10 makes it count).
- **Storage** (`AskCounts`, its own singleton and clock): `<household>/<id>/asks` (`BuiltAppStore.AskCountsFile`),
  a JSON array of `{ "start", "asks" }` per minute still counting, rewritten via `.tmp` + move under a per-app
  semaphore (two asks never both take the last slot; `AskCountsTests` fires 20 at a limit of 5). Minute
  buckets, sparse: an ask counts until 24 h after the start of its minute (at most a minute short of a day),
  so the file holds at most min(cap, 1,440) entries. No cache, so a restart reads the file. Never listed by the
  mount, committed, served or counted in `SizeOf`; a deleted app's directory takes it, and a count racing a
  delete answers 404 `not_found` without making the directory again.
- **The limit**: `Asks:DailyCap`, 200, in `appsettings.json` (held by `AppsShippedSettingsTests`).
  **The seam for 08 is `AskCaps.LimitAsync(app, session, ct)`**, called once per ask just before counting; it
  returns the deployment's value today. 08 reads the override and household default there (on the session's
  assertion) and should change its return to a result that can be unreadable, answered `transient_dependency`
  at the call site in `AgentAsks`, never a lifted cap.
- **The conversation ceiling** stays deployment-wide (10,000); with the cap a household opens at most
  cap x apps conversations a day (4,000 at the defaults), so it can still push other households' out, just
  no longer without bound. Noted in `apps.md`; no per-household bound added.
- **Tests**: `AppsAsksTests` (the Nth ask answered and the next refused naming the limit and `freesAt`, nothing
  sent; refused for its body, its conversation and nobody listening counts nothing; one count across viewers,
  his and the preview, while another app and the neighbour's app count their own; the window rolls at a day
  after the minute; counts outlive a restart over a copy of the volume; fake clock on `AskCounts` alone),
  `AskCountsTests` (concurrency, release, `freesAt` after a lowered limit, a cap of 0, not in `SizeOf`, gone
  with the app), `ToolErrorTaxonomyTests`. The `apps` skill is unchanged (11 teaches the cap).
