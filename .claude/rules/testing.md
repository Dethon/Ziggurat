---
paths:
  - "Tests/**/*.cs"
---

# Testing Rules

## Naming

- Unit/integration test classes: `{ClassUnderTest}Tests.cs`; E2E test classes: `{Feature}E2ETests.cs`
- Test methods use one of two forms, consistent within a suite: `{Method}_{Scenario}_{ExpectedResult}`
  when the test targets one method (`Run_TextNotFound_ThrowsWithSuggestion`), or
  `{Scenario}_{ExpectedResult}` as a behaviour sentence when it pins a rule of a module
  (`AskingForTheFilterTwice_InstallsItOnce`)

## Patterns

- **Prefer integration tests over mocks** — real dependencies via testcontainers; Redis-backed tests
  use `RedisFixture` (a database on a pooled container, `RedisPool`). A class that builds a
  `RedisStackMemoryStore` takes `MemorySearchFixture` instead and takes its vector width from the
  fixture (RediSearch indexes database 0 only)

## Waiting

The suite runs at full width. Wait for the thing, never for a while: `Eventually` and `ArmedClock`
(both in `Tests/`) are there for this.

- **State, not a span.** Background work is waited on by the state it produces (`Eventually.Until`),
  never a sleep followed by an assertion.
- **Armed, then advanced.** Never advance a `FakeTimeProvider` past a timer the code may not have
  created yet (it fires nothing, and the code hangs on a clock already past): take an `ArmedClock`,
  wait for the timer naming the span the production code passes (a loop usually has more than one
  wait outstanding, and matching loosely settles the wrong one), then advance.
- **An absence is bought with time.** A "and nothing further" claim waits out `Eventually.Settle()`;
  pinning both halves, poll for the reachable one, settle, then assert.
- **A cross-thread collection is read through a snapshot.** A harness recording from a background
  loop keeps its list private and hands each reader a copy taken under the lock (a plain `List`
  enumerated while another thread appends throws out of the assertion).
- **A composition fixture injects the clock too.** Register an `ArmedClock`, never
  `TimeProvider.System`, and advance onto each wait. Where the fixture's clock is shared with a real
  socket's waits (Wyoming audio, Kestrel), construct just the service under test with the fake one.
- **A `PeriodicTimer` is not armed per tick**, so `WaitForLiveAsync` cannot tell a loop waiting for
  its next tick from one still working: drive the clock an interval at a time until the effect appears.
- **A TTL is asked for, not inferred.** Read the TTL off the key rather than racing a short expiry.
- **A burst is not the same claim as a race.** Interleave concurrent operations on the suite's shared
  multiplexer in batches, never hundreds at once.
- **An observation beats a margin.** Where nothing on the wire marks a phase's end, expose the state
  internally (`WakeArbiter.IsDeciding`) rather than sleeping a multiple of it, and wait for both edges ("has it closed?" is answered at once
  by a window not yet opened).

## Length

- A simulated span is stepped at the coarsest step that still shows the claim; a fine cadence that is
  a claim of its own gets its own short case
- A class whose cases add up past ten seconds is a chain, even with no container in it: name it in `HeavyFirstCollectionOrderer`, or
  split it
- An address meant to be unreachable is `DeadEnd.Url(...)`, never a port assumed closed (on WSL
  `127.0.0.1:1` is dropped, not refused). The two
  `…WhereNothingIsListening_GivesUpWithoutWaitingOutTheSdkDefault` cases dial `localhost:1` on
  purpose: their claim is the handshake bound, so they need the hang
- A case whose time jumps between runs by a round number is waiting out a timeout, not slowed by load:
  run it alone and time its steps
- An in-process server starts through `TestPort.StartWebAsync`, never `GetAvailable` and a bind of its
  own

## Containers

- A container's bind mount comes from `HostMountDirectory.Claim(name)`, never a fresh temp directory.
  A container that writes its mount as root is emptied through the same image once stopped
  (`TryEmptyAsRootAsync`), as the Home Assistant fixtures do
- What a test writes outside the repository it removes, or keeps in one fixed place and reuses;
  output that comes out the same every run is made once and named by its recipe (`ClipCache`)

## E2E Tests

- Fixtures extend `E2EFixtureBase` (`IAsyncLifetime`), shared across a feature area's classes with
  `[Collection("...")]`. A fixture whose subject is a container rather than a page
  (`SandboxE2EFixture`) implements `IAsyncLifetime` itself and drives `TestHelpers`/`E2EPhase`
  directly, since the base always launches Chromium
- Use `[SkippableFact]` with `Skip.If(...)` to skip when the required stack is unavailable
- Every chat-client page is behind the stack's session check, so a WebChat test opens its page with
  `fixture.CreateSignedInPageAsync(fixture.NextUserIndex(), ...)`; plain `CreatePageAsync` is signed out
  and lands on sign-in. The stack's people are made **before its accounts service starts**
  (`WebChatStack.SeedAccountsStoreAsync`); a test needing a person of its own takes
  `fixture.PersonAsync(name, role)`. A test runs an accounts command in the container
  (`AccountsAsync`) only when the command is its subject. A test that changes who a household's people
  are (a role, sees-all, a member disabled) takes them from the second household
  (`fixture.PersonElsewhereAsync`), since such a change renews every open page of the household. The
  stack is browsed at `localhost` (passkeys' relying party cannot be an IP; the cookie is host-only)
- `fixture.NextUserIndex()` separates a test from its siblings in the same run and from nothing else.
  The index restarts every run and conversations outlive the stack, so name what this run creates with
  a per-run tag (`Guid.NewGuid().ToString("N")[..4]`), early enough in the text to survive the lookup's
  truncation — else an earlier run's row matches too and Playwright's strict mode fails on the pair
- A gesture is input, not timing: a press waits for a control that can take it (a disabled button gets
  no pointer event), clicks go through `ClickThroughApprovalsAsync` (a sibling's approval overlay covers
  the viewport), and a CDP drag stamps its own frames (the app reads flick velocity off event timestamps)
- A wait belongs to the test that can satisfy it: never wait for "nothing anywhere is streaming";
  `WaitForRowsToStopMovingAsync` asks only of rows carrying the caller's tag (`RowSettle`)
- Dismissing an approval overlay is a wait, not a poke: ask until the viewport is clear, and treat an
  overlay whose buttons are not answerable yet as a moment to look again
- The stack's model is `FakeOpenRouter` (below): a test asks in the words its rules read — "Reply with
  only the word X" (several queued or steered together are answered together), "answer only OK", the
  glob-tool demand in `QueueE2E.ToolDemand` for a reply held open on an approval, "a very long and
  detailed story" for one slow enough to stop. A new need is a new rule with a unit test in
  `FakeModelRepliesTests`, never a call to a real model; the fake is shared by every collection, so a
  rule is a pure function of the request and never a queue
- A wait for the model's text needs a turn that ends in text: a turned-down approval ends with none
  (`ApprovalRejection`), so rejecting approvals and then waiting for words can only time out. When the
  claim is only that a turn was stored, wait for the turn (`WaitForTurnKeptAsync`); a long reply is
  stopped once its growth is seen, never idled out
- A new class that waits on a model turn or a browser goes into a collection whose chain is short, or
  one of its own (WebChat: a space name, with `CollectionSlices` raised), never the longest chain;
  compare its collection's span in the run's `.trx` after adding it. A collection made for a feature is
  a chain like any other
- A flag Chromium takes per process (a host-resolver rule naming a port the stack picked) cannot ride the
  shared browser: the fixture declares `OwnBrowserArgs` and gets a browser of its own once its containers
  are up (`AppSpacesE2EFixture`, which maps the apps domain to the stack's Caddy)
- A page's first wait pays for the app's boot, so the boot gets a wait of its own (loading screen gone,
  or composer enabled) before the wait for what the test asked
- A sweep over an app's pages walks them in one page (its links, or `Blazor.navigateTo`), collecting
  failures into one assertion, never a `CreatePageAsync` per route
- To see where a run's wall clock went, set `E2E_TRACE_FILE` (`E2ETrace`) and read it beside the
  `.trx`

## Paid calls

A bare `dotnet test` pays for nothing but the two **canaries** (`[Trait("Category", "Canary")]`,
`Tests/Canary/`): one OpenRouter tool-and-reasoning round trip on a cheap model and one Brave search,
run whenever the keys are in user secrets and skipped otherwise. Every other test that needs a model
talks to `FakeOpenRouter` (`Tests/Integration/Fixtures/`) — in process for integration tests, reached
through the host's gateway by the E2E stack's agent — which answers the Responses wire by rule
(`FakeModelReplies`, a pure function of the request: "reply with only the word X", a demand to call the
glob tool, a very long story streamed slowly enough to stop, a strict schema answered with an instance
of it, anything else one short sentence), or by what a test that owns its fake scripted; Jev's
decisions are answered unavailable, and any route it does not know is refused and kept in `Unhandled`.
A search client is answered with a canned page. `PaidCallsConformanceTests` fails any test outside
`Tests/Canary`, `Tests/Eval` and `Tests/Smoke` that reads user secrets; a probe that measures a real
model's judgment (Jev's labelled sets, the memory models' formats — `Tests/Eval/ModelChecks/`) belongs
to the eval, not to the routine run.
