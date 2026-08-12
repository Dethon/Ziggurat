using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

[Collection("PlaywrightWebBrowserIntegration")]
public class PlaywrightWebBrowserTests(
    PlaywrightWebBrowserFixture fixture,
    ITestOutputHelper testOutputHelper) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        // Clear cookies between tests to ensure isolation
        await fixture.ClearContextStateAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    private string GetUniqueSessionId()
    {
        return $"test-{Guid.NewGuid():N}";
    }

    // Every web_action call in four weeks of production failed at almost exactly 15.0s — the
    // operation timeout — because an unresolvable ref was waited on as if it were merely slow. A ref
    // that is not on the page is stale, so the wait cannot succeed no matter how long it runs; the
    // agent needs to be told to re-snapshot, quickly, rather than after a 15-second silence.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ActionAsync_WithStaleRef_FailsFastWithRefreshHint()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            await fixture.Browser.NavigateAsync(new BrowseRequest(
                SessionId: sessionId, Url: "https://example.com", MaxLength: 5000));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await fixture.Browser.ActionAsync(new WebActionRequest(
                SessionId: sessionId, Ref: "e9999", Action: WebActionType.Click));
            sw.Stop();

            result.Status.ShouldBe(WebActionStatus.ElementNotFound);
            result.ErrorMessage.ShouldNotBeNull().ShouldContain("web_snapshot");
            sw.ElapsedMilliseconds.ShouldBeLessThan(6000);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task NavigateAsync_WithSimplePage_ReturnsContent()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Act
            var request = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com",
                MaxLength: 5000);
            var result = await fixture.Browser.NavigateAsync(request);

            // Assert
            result.Status.ShouldBe(BrowseStatus.Success);
            result.SessionId.ShouldBe(sessionId);
            result.Title.ShouldNotBeNullOrEmpty();
            result.Content.ShouldNotBeNullOrEmpty();
            result.Content.ShouldContain("example");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task NavigateAsync_SessionPersistsAcrossCalls()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            var request1 = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com",
                MaxLength: 1000);
            var result1 = await fixture.Browser.NavigateAsync(request1);
            result1.Status.ShouldBe(BrowseStatus.Success);

            var request2 = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.org",
                MaxLength: 1000);
            var result2 = await fixture.Browser.NavigateAsync(request2);

            // Assert - both navigations should work and session should persist
            result2.Status.ShouldBe(BrowseStatus.Success);
            result2.SessionId.ShouldBe(sessionId);
            result2.Url.ShouldContain("example.org");

            var currentPage = await fixture.Browser.GetCurrentPageAsync(sessionId);
            currentPage.Status.ShouldBe(BrowseStatus.Success);
            currentPage.Url.ShouldContain("example.org");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task GetCurrentPageAsync_WithValidSession_ReturnsContent()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            var browseRequest = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com",
                MaxLength: 2000);
            await fixture.Browser.NavigateAsync(browseRequest);

            var result = await fixture.Browser.GetCurrentPageAsync(sessionId);

            // Assert
            result.Status.ShouldBe(BrowseStatus.Success);
            result.SessionId.ShouldBe(sessionId);
            result.Content.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task CloseSessionAsync_ClosesSession()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();

        var browseRequest = new BrowseRequest(
            SessionId: sessionId,
            Url: "https://example.com",
            MaxLength: 1000);
        await fixture.Browser.NavigateAsync(browseRequest);

        await fixture.Browser.CloseSessionAsync(sessionId);

        var result = await fixture.Browser.GetCurrentPageAsync(sessionId);
        result.Status.ShouldBe(BrowseStatus.SessionNotFound);
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task NavigateAsync_WithNonexistentDomain_ReturnsError()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            var request = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://this-domain-definitely-does-not-exist-xyz123.com",
                MaxLength: 1000);
            var result = await fixture.Browser.NavigateAsync(request);

            result.Status.ShouldBe(BrowseStatus.Error);
            result.ErrorMessage.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task NavigateAsync_ParallelSessions_AllSucceedIndependently()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessions = Enumerable.Range(0, 4).Select(_ => GetUniqueSessionId()).ToList();

        // Four live pages, none of them heavy. What this pins is that four sessions navigate at
        // once without landing in each other's pages, and the check for that is the host each one
        // reports — which an encyclopaedia article answers no better than a page of boilerplate
        // does. Two of these used to be full articles, 800KB and up, and the pair of them was most
        // of the test's seven seconds; Special:BlankPage is 34KB of the same site. The snapshot
        // case below still loads one real article.
        var urls = new[]
        {
            "https://example.com",
            "https://example.org",
            "https://example.net",
            "https://en.wikipedia.org/wiki/Special:BlankPage"
        };

        try
        {
            var navigateTasks = sessions.Select((sid, i) =>
                fixture.Browser.NavigateAsync(new BrowseRequest(
                    SessionId: sid,
                    Url: urls[i],
                    MaxLength: 5000))).ToList();

            var results = await Task.WhenAll(navigateTasks);

            results.ShouldAllBe(r => r.Status == BrowseStatus.Success || r.Status == BrowseStatus.Partial);

            for (var i = 0; i < sessions.Count; i++)
            {
                results[i].SessionId.ShouldBe(sessions[i]);
                results[i].Url.ShouldContain(new Uri(urls[i]).Host);
            }

            // Verify sessions didn't cross-contaminate via GetCurrentPageAsync in parallel
            var pageTasks = sessions.Select(sid =>
                fixture.Browser.GetCurrentPageAsync(sid)).ToList();
            var pages = await Task.WhenAll(pageTasks);

            for (var i = 0; i < sessions.Count; i++)
            {
                pages[i].Status.ShouldBe(BrowseStatus.Success);
                pages[i].Url.ShouldContain(new Uri(urls[i]).Host);
            }
        }
        finally
        {
            await Task.WhenAll(sessions.Select(sid => fixture.Browser.CloseSessionAsync(sid)));
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task SnapshotAsync_ParallelSessions_ReturnIndependentSnapshots()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessions = Enumerable.Range(0, 3).Select(_ => GetUniqueSessionId()).ToList();

        // Three live pages whose content differs, because "the snapshots are not each other's" is
        // the whole assertion — three copies of the same boilerplate would pass it by accident, and
        // three encyclopaedia articles spent fifteen seconds proving it. One real article stays:
        // walking an accessibility tree of that size is worth doing once, and this is the case that
        // does it.
        var urls = new[]
        {
            "https://example.com",
            "https://en.wikipedia.org/wiki/Special:BlankPage",
            "https://en.wikipedia.org/wiki/C_Sharp_(programming_language)"
        };

        try
        {
            // Set up: navigate each session to its page in parallel. The browser handles concurrent
            // sessions (see NavigateAsync_ParallelSessions); navigating sequentially here needlessly
            // serialized three live-site loads and dominated this test's wall-clock.
            var setupResults = await Task.WhenAll(sessions.Select((sid, i) =>
                fixture.Browser.NavigateAsync(new BrowseRequest(
                    SessionId: sid,
                    Url: urls[i],
                    MaxLength: 1000))));
            setupResults.ShouldAllBe(r => r.Status == BrowseStatus.Success || r.Status == BrowseStatus.Partial);

            var snapshotTasks = sessions.Select(sid =>
                fixture.Browser.SnapshotAsync(new SnapshotRequest(SessionId: sid))).ToList();

            var snapshots = await Task.WhenAll(snapshotTasks);

            for (var i = 0; i < sessions.Count; i++)
            {
                snapshots[i].SessionId.ShouldBe(sessions[i]);
                snapshots[i].Snapshot.ShouldNotBeNullOrEmpty();
                snapshots[i].RefCount.ShouldBeGreaterThan(0);
                snapshots[i].Url!.ShouldContain(new Uri(urls[i]).Host);

                testOutputHelper.WriteLine($"Session {i} ({urls[i]}): {snapshots[i].RefCount} refs, " +
                                           $"snapshot length: {snapshots[i].Snapshot!.Length}");
            }

            // Snapshots should be different from each other (different pages)
            snapshots[0].Snapshot.ShouldNotBe(snapshots[1].Snapshot);
            snapshots[1].Snapshot.ShouldNotBe(snapshots[2].Snapshot);
        }
        finally
        {
            await Task.WhenAll(sessions.Select(sid => fixture.Browser.CloseSessionAsync(sid)));
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ActionAsync_ParallelSessions_ExecuteIndependently()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessions = Enumerable.Range(0, 3).Select(_ => GetUniqueSessionId()).ToList();
        // Use lightweight pages so element actions resolve quickly under parallel load
        var urls = new[]
        {
            "https://example.com",
            "https://example.com",
            "https://example.com"
        };

        try
        {
            // Set up: navigate each session in parallel (see NavigateAsync_ParallelSessions).
            var setupResults = await Task.WhenAll(sessions.Select((sid, i) =>
                fixture.Browser.NavigateAsync(new BrowseRequest(
                    SessionId: sid, Url: urls[i], MaxLength: 500))));
            setupResults.ShouldAllBe(r => r.Status == BrowseStatus.Success);

            // Snapshot to assign element refs
            var snapshotTasks = sessions.Select(sid =>
                fixture.Browser.SnapshotAsync(new SnapshotRequest(SessionId: sid))).ToList();
            var snapshots = await Task.WhenAll(snapshotTasks);

            snapshots.ShouldAllBe(s => s.RefCount > 0);

            // Execute hover actions in parallel on e1 (lightweight page, fast element resolution)
            var actionTasks = sessions.Select(sid =>
                fixture.Browser.ActionAsync(new WebActionRequest(
                    SessionId: sid,
                    Ref: "e1",
                    Action: WebActionType.Hover))).ToList();

            var actionResults = await Task.WhenAll(actionTasks);

            for (var i = 0; i < sessions.Count; i++)
            {
                actionResults[i].SessionId.ShouldBe(sessions[i]);
                actionResults[i].Status.ShouldBe(WebActionStatus.Success);
                actionResults[i].Snapshot.ShouldNotBeNullOrEmpty();

                testOutputHelper.WriteLine($"Session {i}: status={actionResults[i].Status}, " +
                                           $"url={actionResults[i].Url}");
            }

            // Verify sessions remain independent after actions
            var pageTasks = sessions.Select(sid =>
                fixture.Browser.GetCurrentPageAsync(sid)).ToList();
            var pages = await Task.WhenAll(pageTasks);

            pages.ShouldAllBe(p => p.Status == BrowseStatus.Success);
            pages.ShouldAllBe(p => p.Url!.Contains("example.com"));
        }
        finally
        {
            await Task.WhenAll(sessions.Select(sid => fixture.Browser.CloseSessionAsync(sid)));
        }
    }

}