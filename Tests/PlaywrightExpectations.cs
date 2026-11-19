using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace Tests;

internal static class PlaywrightExpectations
{
    // What an expectation that names no timeout waits for: an admin page's answer to a save crosses
    // the app, the proxy, the session check and the accounts service, and two suites running at once
    // took that past Playwright's five seconds. An expectation returns as soon as it holds, so the
    // larger cap costs a passing test nothing; every wait that relies on timing out quickly names its
    // own. Set as the assembly loads: it was set by the E2E fixture's browser launch, so an
    // integration test's page that ran before any E2E fixture (the passkey ceremonies) had five.
    [ModuleInitializer]
    internal static void WaitAsLongAsALoadedRunNeeds() => Assertions.SetDefaultExpectTimeout(20_000);
}