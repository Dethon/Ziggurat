---
paths:
  - "Tests/**/*.cs"
---

# Testing Rules

## Naming

- Unit/integration test classes: `{ClassUnderTest}Tests.cs`; E2E test classes: `{Feature}E2ETests.cs`
- Test methods use one of two forms. Pick one per suite and stay consistent within it:
  - `{Method}_{Scenario}_{ExpectedResult}` when the test targets one method:
    `Run_TextNotFound_ThrowsWithSuggestion`
  - `{Scenario}_{ExpectedResult}` as a behavior sentence, when the test pins a rule of a
    module rather than one method (the style of the contract and channel suites):
    `AskingForTheFilterTwice_InstallsItOnce`

## Patterns

- **Prefer integration tests over mocks** — test real behavior with real dependencies via
  testcontainers; Redis-backed tests use `RedisFixture`
- `RedisFixture` is a database on a pooled container, not a container of its own (`RedisPool`).
  A class that builds a `RedisStackMemoryStore` takes `MemorySearchFixture` instead — RediSearch
  only indexes database 0, so those classes share it and take their vector width from the fixture

## Waiting

The suite runs at full width, so a test that waits competes for a thread with everything else.
Wait for the thing, never for a while. `Eventually` and `ArmedClock` (both in `Tests/`) are there
for this, and each shape below is one that has already failed here.

- **State, not a span.** A test that sets background work going waits for the state that work
  produces (`Eventually.Until`). A sleep followed by an assertion is a claim about the machine, and
  the run where that claim stops holding reports it as a behaviour failure. Polling returns the
  moment the state arrives, so it is also faster than the sleep it replaces.
- **Armed, then advanced.** Advancing a `FakeTimeProvider` past a timer the code has not created
  yet fires nothing, and the code then waits out a clock already beyond it — a hang, surfacing as
  whatever failed to happen next. Take an `ArmedClock` and wait for the timer first, naming the
  span the production code passes: a loop usually has more than one wait outstanding, and matching
  loosely settles the wrong one.
- **An absence is bought with time.** Nothing announces what did not happen, so a "and nothing
  further" claim waits out `Eventually.Settle()`. A test pinning both halves — it reaches six and
  stops there — polls for the six, settles, then asserts the six.
- **A cross-thread collection is read through a snapshot.** A harness recording from a background
  loop keeps its list private and hands each reader a copy taken under the lock. A plain `List`
  enumerated while another thread appends throws out of the assertion, blaming whatever was being
  checked.
- **A composition fixture injects the clock too.** Production code here takes `TimeProvider` for
  every wait, and a fixture that registers `TimeProvider.System` hands all of that back: the WebChat
  client's rebuild path spends three real 500ms retries before it gives up, and the test asserting
  on what it does then was racing 1.5s it could not see. Register an `ArmedClock` and advance onto
  each wait. Where a fixture's clock is shared with something whose waits belong to a real socket —
  audio streaming down a Wyoming connection, Kestrel — construct just the service under test with
  the fake one rather than swapping the singleton.
- **A `PeriodicTimer` is not armed per tick.** It arms once and reuses that timer, so
  `WaitForLiveAsync` is answered continuously and cannot separate a loop waiting for its next tick
  from one still working through the last. An advance landing in that gap is consumed and the timer
  never comes due. Drive the clock an interval at a time until the effect appears.
- **A TTL is asked for, not inferred.** A test that writes a short expiry and then does anything
  before refreshing it is racing that expiry, and losing the race reads as a behaviour failure.
  Read the TTL off the key: it is faster, and it pins the expiry the refresh actually asked for
  rather than merely proving it outlived a sibling.
- **A burst is not the same claim as a race.** Two hundred concurrent operations on the multiplexer
  the whole suite shares tripped its internal failure, and the test died on a connection error
  instead of on the thing it was pinning. Interleave in batches — the race is just as real.
- **An observation beats a margin.** Where a test has to let one phase finish before starting the
  next and nothing on the wire marks it, expose the state internally (`WakeArbiter.IsDeciding`,
  beside the `IsRegistered` that was already there) rather than sleeping a multiple of it. Wait for
  both edges: asking only whether a window has closed is answered instantly by one that has not
  opened yet.

## Length

- A simulated span is stepped at the coarsest step that still shows the claim. Thirteen hours of
  polls a minute apart (780 round trips, twenty seconds) proved what polls five minutes apart
  prove in one; the one-minute cadence is a claim of its own and gets its own short case
- A class whose cases add up past ten seconds is a chain, even with no container in it: xUnit
  offers threads in discovery order, so a long class late in it starts near the run's end and ends
  it. Name it in `HeavyFirstCollectionOrderer`, or split it
- An address meant to be unreachable is `DeadEnd.Url(...)`, a listener that hangs up, never a port
  assumed closed. On this machine a connect to `127.0.0.1:1` is dropped, not refused, and waits out
  the caller's whole connect timeout: the eval browser's proxy paid eight seconds a page, a failed
  reconnect ten. The two `…WhereNothingIsListening_GivesUpWithoutWaitingOutTheSdkDefault` cases
  dial `localhost:1` on purpose: their claim is the handshake bound, so they need the hang
- A case whose time jumps between runs by a round number (2 s one run, 17 s the next) is waiting
  out a timeout somewhere, not slowed by load: run it alone a few times and time its steps. The
  eval's archive case paid fifteen seconds one run in four to a Chromium lifecycle wait that missed
  its event — a defect in the browser code, which the test then pinned

- An in-process server starts through `TestPort.StartWebAsync`, never `GetAvailable` and a bind of
  its own. The claim keeps test processes apart and nothing keeps out the rest of the machine: under
  WSL's mirrored networking Windows shares this port space, and a fake model server beside a second
  suite died on "address already in use" at a port the probe had just found free

## Containers

- A container's bind mount comes from `HostMountDirectory.Claim(name)`, never a fresh temp
  directory. Docker Desktop on WSL keeps a staging mount for every source path it has seen, for
  good, and each one makes every later container start slower, so a new path per test slows every
  run after it. A stable path reuses its staging. A container that writes its mount as root leaves
  files this process cannot remove: empty the directory through the same image once the container
  has stopped (`TryEmptyAsRootAsync`), as the Home Assistant fixtures do
- What a test writes outside the repository it removes, or keeps in one fixed place and reuses. A
  fresh directory per run that nothing deletes grows `/tmp` for good: the test clips alone reached
  5 GB. Output that comes out the same every run, such as an encoded clip, is made once and kept,
  named by what it was made from (`ClipCache`)

## E2E Tests

- Fixtures extend `E2EFixtureBase` (`IAsyncLifetime`), which manages browser lifecycle and
  container startup; share a fixture across a feature area's test classes with `[Collection("...")]`.
  A fixture whose subject is a container rather than a page (`SandboxE2EFixture` — the sandbox
  image's mount-point alias, reached over MCP) implements `IAsyncLifetime` itself and drives
  `TestHelpers`/`E2EPhase` directly: the base always launches Chromium, and a browser nothing
  navigates is the cost that capped how far this suite could be split
- Use `[SkippableFact]` with `Skip.If(...)` to skip when the required stack is unavailable
- Every chat-client page is behind the stack's session check, so a WebChat test opens its page with
  `fixture.CreateSignedInPageAsync(fixture.NextUserIndex(), ...)`: the index names an account with a
  session, and the cookie is on the page's context before anything is opened. The stack's people are
  made **before its accounts service starts** (`WebChatStack.SeedAccountsStoreAsync`: the store built
  in the test process with the service's own stores and copied in) — each collection's first users by
  index, a probe, and a pool a test that needs a person of its own takes with
  `fixture.PersonAsync(name, role)` (renamed on their own account page). An accounts command in the
  container is two .NET processes starting per person; those were the E2E run's floor, so a test
  runs one (`AccountsAsync`) only when the command is its subject — a link, a second session. A test
  that changes who a household's people are (a role, sees-all, a member disabled) takes its people
  from the stack's second household (`fixture.PersonElsewhereAsync`): such a change has every open
  page of the household renew its connection, which in the home caught other collections mid-way. A
  page from plain `CreatePageAsync` is signed out and lands on sign-in. The stack is browsed at
  `localhost` on a port it takes up front, because the passkeys' relying party cannot be an IP and
  the session cookie is host-only.
- `fixture.NextUserIndex()` gives a test its own user, which separates it from its siblings in the
  same run and from nothing else: the index restarts every run and conversations outlive the stack.
  Name what this run creates with a per-run tag (`Guid.NewGuid().ToString("N")[..4]`), placed early
  enough in the text to survive whatever truncation the lookup does — otherwise an earlier run's
  row answers as well, and Playwright's strict mode fails on the pair rather than waiting
- A gesture is input, not timing. A disabled button receives no pointer event at all, so a press
  waits for a control that can take it; an approval overlay leaked by a sibling covers the whole
  viewport, so clicks go through `ClickThroughApprovalsAsync`; and the app reads flick velocity off
  event timestamps, so a CDP drag stamps its own frames rather than being stamped on arrival
- A wait belongs to the test that can satisfy it. Conversations in a space outlive the test that
  made them, so "nothing anywhere is streaming" is a claim about siblings' replies this test cannot
  finish — one that never completes then costs every later wait its whole cap.
  `WaitForRowsToStopMovingAsync` takes the caller's tag for that reason and asks the streaming
  question only of the rows carrying it; `RowSettle` is the rule on its own, unit-tested
- Dismissing an approval overlay is a wait, not a poke. The overlay rides a fire-and-forget resume
  chain, so a sibling's can arrive at any moment — including after a test has checked for one. Ask
  until the viewport is clear, and treat "an overlay is up but its buttons are not answerable yet"
  as a moment to look again rather than as dismissed
- The stack's model is `FakeOpenRouter`, answering by rule (`FakeModelReplies`), never a provider:
  a test asks for what it needs in the words the rules read — "Reply with only the word X" (several
  such messages queued or steered together are answered together), "answer only OK", the glob-tool
  demand in `QueueE2E.ToolDemand` for a reply held open on an approval, "a very long and detailed
  story" for one streamed slowly enough to stop — and a new need is a new rule with a unit test in
  `FakeModelRepliesTests`, never a call to a real model. The fake is shared by every collection, so
  a rule is a pure function of the request and never a queue
- A wait for the model's text needs a turn that ends in text. A turned-down approval ends the turn
  with no text (`ApprovalRejection`), so a wait that rejects approvals and then waits for words can
  only time out. When the claim is only that a turn was stored, wait for the turn
  (`WaitForTurnKeptAsync`), not its words. When the claim is that a long reply grew or streamed,
  stop the reply once that is seen; don't idle out the rest of it
- Tests in one collection run one after another, so where a new class goes matters more to the
  run's length than what it does. A class that waits on a model turn or a browser goes into a
  collection whose chain is short, or into one of its own (for WebChat: a space name, with
  `CollectionSlices` raised). It never joins the longest chain. After adding one, compare its
  collection's span in the run's `.trx` with the others'. A collection made for a feature is a
  chain like any other: the queue's grew three classes of multi-turn cases and became the run's
  longest by ten seconds, when a class of it on the shortest existing chain cost nothing
- A page's first wait pays for the app's boot, so the boot gets a wait of its own (the loading screen
  gone, or the composer enabled) before the wait for what the test asked. A collection's first case
  boots while every other collection boots, and one 60 s cap meant for the boot and a 10 s answer
  together ran out on the boot alone beside a second suite
- A sweep over an app's pages walks them in one page. Each `CreatePageAsync` boots the Blazor app
  again, and the boot is most of what such a case costs: a theory with a page per route booted the
  dashboard twelve times to check headers and widths one boot shows. Open the first page at its
  address and reach the rest inside the app (its links, or `Blazor.navigateTo`), collecting what
  fails across pages into one assertion
- What a run spent its wall clock on is answerable rather than guessable: set `E2E_TRACE_FILE` and
  every fixture phase, container start and row-settle wait appends its seconds to that file
  (`E2ETrace`, a null check when the variable is unset). Read it beside the run's `.trx` — the trx
  says which test was slow, the trace says which wait inside it was

## Paid calls

A bare `dotnet test` pays for nothing but the two **canaries** (`[Trait("Category", "Canary")]`, `Tests/Canary/`): one OpenRouter tool-and-reasoning round trip on a cheap model and one Brave search, run whenever the keys are in user secrets and skipped otherwise, so a change on either API's side fails a routine run before it fails the deployment. Every other test that needs a model talks to `FakeOpenRouter` (`Tests/Integration/Fixtures/`) — in process for integration tests, reached through the host's gateway by the E2E stack's agent — which answers the Responses wire by rule (`FakeModelReplies`, a pure function of the request: "reply with only the word X", a demand to call the glob tool, a very long story streamed slowly enough to stop, a strict schema answered with an instance of it, anything else one short sentence), or by what a test that owns its fake scripted; Jev's decisions are answered unavailable, and any route it does not know is refused and kept in `Unhandled`. A search client is answered with a canned page. `PaidCallsConformanceTests` fails any test outside `Tests/Canary`, `Tests/Eval` and `Tests/Smoke` that reads user secrets; a probe that measures a real model's judgment (Jev's labelled sets, the memory models' formats — `Tests/Eval/ModelChecks/`) belongs to the eval, not to the routine run.
