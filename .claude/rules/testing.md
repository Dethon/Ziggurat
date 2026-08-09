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
- Use `Shouldly` for assertions (`result.ShouldBe()`, `Should.Throw<>()`)
- Create testable wrappers for classes with protected methods
- Use `IDisposable` for cleanup of temp files/directories

## E2E Tests

- Fixtures extend `E2EFixtureBase` (`IAsyncLifetime`), which manages browser lifecycle and
  container startup; share a fixture across a feature area's test classes with `[Collection("...")]`
- Use `[SkippableFact]` with `Skip.If(...)` to skip when the required stack is unavailable
- Each test gets a unique user identity via `fixture.NextUserIndex()` to avoid state collisions
