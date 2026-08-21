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

## E2E Tests

- Fixtures extend `E2EFixtureBase` (`IAsyncLifetime`), which manages browser lifecycle and
  container startup; share a fixture across a feature area's test classes with `[Collection("...")]`.
  A fixture whose subject is a container rather than a page (`SandboxE2EFixture` — the sandbox
  image's mount-point alias, reached over MCP) implements `IAsyncLifetime` itself and drives
  `TestHelpers`/`E2EPhase` directly: the base always launches Chromium, and a browser nothing
  navigates is the cost that capped how far this suite could be split
- Use `[SkippableFact]` with `Skip.If(...)` to skip when the required stack is unavailable
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
- A wait on a real model is bounded by the provider, not the app. The reply in
  `SendMessage_AppearsInChat` crosses the network to a live LLM; when that cap is exceeded the
  failure must say so, or an ordinary slow generation is read as the client failing to render
- What a run spent its wall clock on is answerable rather than guessable: set `E2E_TRACE_FILE` and
  every fixture phase, container start and row-settle wait appends its seconds to that file
  (`E2ETrace`, a null check when the variable is unset). Read it beside the run's `.trx` — the trx
  says which test was slow, the trace says which wait inside it was
