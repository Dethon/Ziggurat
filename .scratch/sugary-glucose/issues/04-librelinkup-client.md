# 04: LibreLinkUp client and fake

**What to build:** `LibreLinkUpClient` in McpServerSugary and `FakeLibreLinkUp` in Tests/Integration/Fixtures. Login (headers `product: llu.android`, `version: 4.16.0`), region redirect re-login, `Account-Id` = sha256 hex of `user.id`, token cached until `expires`, re-login on 401. `connections` and `graph` parsed into typed readings (`FactoryTimestamp` UTC as the instant, `ValueInMgPerDl`, `TrendArrow` mapped) and sensor facts (serial, activation). Typed failures: credentials refused (status 2), step required (4, with the component name), version too old (920), rate limited (429/430 with `Retry-After`), unreachable. Spec: "Glucose from LibreLinkUp — Client".

**Blocked by:** None.

Status: done

- [x] Against the fake: login, redirect, headers, connections, graph, token reuse across calls, one re-login on 401.
- [x] Each failure maps to its typed result; `Retry-After` is surfaced.
- [x] Trend arrows 1–5 and an unknown value map as the spec says; timestamps parse invariant-culture.

## Comments

What was built:

- `McpServerSugary/Glucose/`: `LibreLinkUpClient` (`ConnectionsAsync(account, ct)`, `GraphAsync(account, patientId, ct)`), the answer and failure unions (`LibreLinkUpAnswer<T>.Ok(Value, Region)` / `.Failed(Failure)`; `LibreLinkUpFailure.CredentialsRefused | StepRequired(Component) | VersionTooOld(MinimumVersion) | RateLimited(RetryAfter?) | Unreachable(Reason)`), `LibreLinkUpWire` (the tolerant parsing, the arrow map, the invariant-culture stamp), `LibreLinkUpRegions` (`Abbott`, with `ru` at `api.libreview.ru`), and `AddLibreLinkUp(regions)`, which `ConfigureSugary` calls with `Abbott`.
- `Domain/DTOs/Sugary/Glucose.cs`: `GlucoseTrend` and `SensorReading(At, MgPerDl, Trend)`, for the trigger engine later. It is not named `GlucoseReading`, which ticket 06 gives its stored row.
- `Tests/Integration/Fixtures/FakeLibreLinkUp.cs`: every region at once under a path prefix (`fake.Regions`), the redirect, tokens good only at their region and until their `expires` on the test's clock, a login refused 920 without the app's product and version, a 401 without the right `Account-Id`, gzipped answers, plus knobs for ticket 06 (`RevokeTokens`, `RefuseEveryToken`, `TokenLifetime`, `MinimumVersion`, `Account.Step`, `AlterTicket`, `InterruptNext(status, body, headers)`, `AnswerNext(body)`, `Connection.Latest/Graph/Sensors`). Interruptions answer the next request whatever its path, in order.
- Tests: `Tests/Integration/Clients/LibreLinkUpClientTests.cs` (36) and `Tests/Unit/McpServerSugary/LibreLinkUpWireTests.cs` (23).

Decisions and deviations:

- **Token cache key** is a SHA-256 of the lowercased email and the password, so a wrong password is never answered from the right one's token (ticket 05's validation logs in for real), and the cache holds no password. The region found is kept with the token and returned on every `Ok`, so the link keeps it.
- **One login per account at a time** (a per-key semaphore): a poll and a link being saved may ask at once.
- **Expiry** is checked before a call, five minutes early, so a stale token is replaced without a 401. A 401 **or 403** on a call re-logs once; a token refused even fresh is `Unreachable`, not `CredentialsRefused`, because typing the password again would not fix it and the poller stops a link on a refusal. A 401/403 on the login itself is also `Unreachable`: the service answers bad credentials as HTTP 200 with status 2.
- **Unknown body statuses, 5xx, HTML bodies and timeouts** are `Unreachable` (retryable). Caller cancellation propagates as cancellation.
- **A body of the wrong shape fails typed, never with an exception.** Fields are read through an object-only accessor, and epochs are range-guarded, so a millisecond `a` is no activation. A connection's malformed `sensor` or `glucoseMeasurement` is absent. A connections list with any entry that is not a connection is `Unreachable`, never a shorter list: a shorter list could read as "no connections" or drop the person she follows.
- **A ticket whose expiry is unreadable** (no `expires` or `duration`, or out of range) is kept until a call is refused. Treating it as expired would log in on every call.
- **`Retry-After`** is read as a delta or a date (against the `TimeProvider`), and is null when absent.
- **A redirect's region must be a bare code** (`^[a-z]{2,3}[0-9]?$`). Otherwise the answer could send her password to whatever host it named. Only one redirect is followed.
- **Headers:** `accept-encoding: gzip` comes from the handler (`LibreLinkUpClient.CreateHttp()`, `AutomaticDecompression`), which also undoes it. `content-type: application/json` goes on the login POST only, because .NET will not put a content header on a bodiless GET. `cache-control: no-cache` and `connection: Keep-Alive` go on every request.
- **The client is a singleton** (`AddLibreLinkUp`), not an `AddHttpClient<T>` typed client. A typed client is transient and would lose its tokens; a test resolves it twice and sees one login.
- **For ticket 06:** a token refused even fresh costs one login on every later call, until the account is fixed. The poller must back off on repeated `Unreachable`, or it logs in every minute. A graph call for a patient the account no longer follows is `Unreachable` here; that is the fake's guess (`data: null`), not verified Abbott behaviour.
- **Not built:** adopting the refreshed `ticket` some answers carry (no community client does), and dropping a cached token on unlink (it is keyed by the password and dies with the process).
- **Tests were written before the client**, but the first run was green, so each guard was proven by mutation instead. Taking out decompression, the password in the key, the refused-token check, the expiry check and the login gate each turned its test red. The wrong-shape and unreadable-expiry tests were added after review and were red (9 failures) before the fix.
