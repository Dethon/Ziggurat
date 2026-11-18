# 05: A person links their LibreLinkUp account

**What to build:** `GlucoseLink` (`IPersonal`) and migration; password sealed with AES-GCM under `Sugary:SealingKey` (secret: `.env` placeholder `SUGARY_SEALING_KEY`, compose env), associated data the owner's ids. Routes under `/api/glucose/link`: put (validates by logging in and listing connections; one connection chosen, several returned to choose), choose connection, settings (unit, target low/high), get (never the password), delete (forgets credentials, keeps readings). Writes go through `OneLogWriteAtATime`. Spec: "The link".

**Blocked by:** 04

Status: done

- [x] Linking with refused credentials, a terms step or no connections answers a typed refusal the app can word.
- [x] Two connections → the person chooses; the chosen `patientId` is kept.
- [x] The sealed password round-trips only with the right owner and key; no route returns it.
- [x] Unlink deletes the link and keeps readings; `MigrationsMatchTheModelTests` green.

## Comments

What was built:

- `McpServerSugary/Store/GlucoseLink.cs`: `GlucoseLink` (`IPersonal`, key `UserId`, one per person): `Email`, `SealedPassword` (bytes), `Region` (lowercase code, null = global), `PatientId` (null until chosen), `Connections` (everyone the account followed at link time, child table `GlucoseLinkConnections`: `PatientId`, `Name`), `Unit` (`GlucoseUnit.Mgdl|Mmol`, stored as text), `TargetLow`/`TargetHigh` (int mg/dL, default 70/180), `LinkedAt`, and for the poller `LastSuccessAt`, `LastFailure` (`GlucoseLinkFailure`: `CredentialsRefused|StepRequired|VersionTooOld|RateLimited|Unreachable`, text), `FailureDetail` (a step's component, a minimum version), `FailingSince`. `Name` is computed from the chosen connection. `GlucoseRange` holds the defaults and the 40..400 check. Migration `20261118004812_GlucoseLinks`.
- `McpServerSugary/Glucose/GlucoseCredentials.cs`: `Seal(Account owner, string password)` / `Open(Account owner, byte[] sealed) -> string?` over `Domain.Security.Sealing.SealingKey`; associated data is the JSON array `["sugary-glucose-link", tenantId, userId]`, so no two owners' ids join the same. `Open` is null for no key, another key, or another owner; `CanSeal` says whether there is a key. Registered from `SealingKey.Parse(settings.SealingKey, "SealingKey")`: empty is off, malformed stops startup.
- `McpServerSugary/App/GlucoseEndpoints.cs`, under `/api/glucose`, each refusal a problem with a `code`:
  - `GET /link` -> 200 link; 404 `not-linked`; 503 `linking-off` when there is no link and no key (a link kept from before is still shown without the key).
  - `PUT /link` `{email, password, region?}` -> 200 link (one connection chosen; several offered with `patientId` null; a re-link keeps her unit, range and a choice the account still follows). 400 `missing-credentials`, `unknown-region` (not a bare code; LibreLinkUp is not asked), `credentials-refused`, `step-required` (+`component`), `no-connections`; 429 `rate-limited` (+`retryAfterSeconds`, `Retry-After` header when known); 502 `unavailable` (unreachable, and an app version too old, which is logged as a warning); 503 `linking-off`. A refused link writes nothing; the link she had stays.
  - `PUT /link/connection` `{patientId}` -> 200 link; 400 `unknown-connection` (not among those offered; no second login); 404 `not-linked`.
  - `PUT /settings` `{unit: "mgdl"|"mmol", targetLow, targetHigh, loaded?}` -> 200 link; 400 `unknown-unit`, `bad-range` (40 <= low < high <= 400, mg/dL whatever the unit); 404 `not-linked`; 409 when `loaded` clashes (`StaleEdits`, as every sheet save).
  - `DELETE /link` -> 204; 404 `not-linked`.
  - The link answer: `email, region, patientId, name, connections[{patientId, name}], unit, targetLow, targetHigh, linkedAt, lastSuccessAt, lastFailure (a code as above), failureDetail, failingSince`. Never the password.
- `OneLogWriteAtATime` covers `/glucose`. `PUT /link` is marked `TakesItsOwnTurn`, logs in outside the queue and takes her turn (`TurnAsync(account, ct)`) around the read and upsert alone, so a slow login does not hold her meals.
- Secret: `SugarySettings.SealingKey`, compose `SEALINGKEY: ${SUGARY_SEALING_KEY}` on `mcp-sugary` only (`DeploymentConformanceTests` row), `.env` placeholder. The E2E stack and tenant kit set nothing: empty is linking off.
- Tests: `Tests/Integration/McpServerTests/SugaryGlucoseLinkApiTests.cs` (31), `Tests/Unit/McpServerSugary/GlucoseCredentialsTests.cs` (7). Sugary + unit tier: 7847 passed, 0 failed.

Decisions and deviations:

- **The config path is `SealingKey` at the settings root** (env `SEALINGKEY`), not a `Sugary:` section: every Sugary setting binds at the root (`DatabasePath`, `PenReminderHour`, `WEBPUSH__*`), so "Sugary:" in the spec reads as the server's name. Ticket 06's `PollSeconds` should follow the same way (`Glucose:PollSeconds`, or a root key).
- **Targets default to 70/180, never the connection's.** The connection's targets are usually the LibreLink app's defaults, the spec says 70/180, and time-in-range is reported against the consensus range; she sets her own in Sugary.
- **Her email is answered in full**: only she reads her link, and she typed it.
- **`LastSuccessAt` is set on linking**, since LibreLinkUp answered then; failure fields are cleared.
- **The connection list is stored on every link**, one or several, so the choice is checked without logging in again and she can change it later.
- **Version too old is `unavailable` to her** (the operator's to fix), logged with the minimum version.
- **The migration's snapshot was regenerated whole** by the design package now on EF 11 (file-scoped namespace, `ProductVersion` 11.0.0); the model diff is the two new tables. The class EF named `_20261118004812_GlucoseLinks` was renamed `GlucoseLinks`.

For ticket 06 (the poller):

- Read links through the factory context with `IgnoreQueryFilters()` (and add the poller to the rules' list of those that do), `Include(l => l.Connections)` only if needed. A live link has `PatientId != null` and is not stopped on `LastFailure == CredentialsRefused` (that one waits for her to link again; a re-link clears it).
- The password: `GlucoseCredentials.Open(new Account(row.TenantId, row.UserId), row.SealedPassword)`; null means no key or a key changed, so skip the link (and say so once in the log, never the password; `LibreLinkUpAccount` is a record whose `ToString` prints it). Region comes off the row: `new LibreLinkUpAccount(row.Email, password, row.Region)`.
- Fill `LastSuccessAt`, `LastFailure`, `FailureDetail`, `FailingSince` (set once on the first failure, cleared on a success). `GlucoseEndpoints.FailureCode` maps a stored failure to the app's code.
- **"Unlink keeps readings" is untested here**: there is no readings table yet. When `GlucoseReading` lands, add to `Unlinking_ForgetsTheLinkAndLeavesHerLog` that her readings survive (no FK from readings to the link).
- **Writing a link's state from the poller races her `PUT /link`.** A poll that started with the old password can finish after she re-linked and write `CredentialsRefused` onto the fresh link, stopping a good one; neither her turn nor a columns-only update prevents that alone. Write the state only where `LinkedAt` is still the value the poll read (it changes on every successful link): `ExecuteUpdateAsync` with `Where(l => l.UserId == … && l.LinkedAt == read)`, or the check inside her turn.
- **Snapshot:** regenerated whole on EF 11, so a parallel branch adding a Sugary migration must regenerate on top of `20261118004812_GlucoseLinks`.
- **Deploy:** production's `.env` needs `SUGARY_SEALING_KEY` (`openssl rand -base64 32`) before anyone can link. There is no rotation: a changed or lost key means everyone links again.

Red first: the tests were written before the code, but the code was then written before a run, so the first run was red only on compile and on one test's own wrong expectation (a login count that forgot the region redirect). Each guard was then proven by mutation: taking out the write turn (two links at once kept two rows, a 500), keeping the typed region instead of the found one, dropping the offered-list check, and dropping the owner ids from the associated data each turned their tests red.
