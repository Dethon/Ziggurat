using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

[Collection(PlaywrightCollections.SharedBrowser)]
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
            // Partial counts throughout this case: what it pins is that the session survives two
            // navigations and ends up on the second URL, and a DOMContentLoaded that does not
            // arrive returns Partial with the page present and the session perfectly intact. The
            // hosts are reached over the network from inside the container, so demanding Success
            // made a slow third party read as the session failing to persist.
            var result1 = await fixture.Browser.NavigateAsync(request1);
            result1.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);

            var request2 = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.org",
                MaxLength: 1000);
            var result2 = await fixture.Browser.NavigateAsync(request2);

            // Assert - both navigations should work and session should persist
            result2.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
            result2.SessionId.ShouldBe(sessionId);
            result2.Url.ShouldContain("example.org");

            var currentPage = await fixture.Browser.GetCurrentPageAsync(sessionId);
            currentPage.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
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
            // Partial counts, as it does for the sibling above: this is setup, and the subject is
            // what the refs do afterwards. example.com is reached over the network from inside the
            // container, and a DOMContentLoaded that does not arrive returns Partial with the page
            // perfectly present — 167 characters of it — after spending the production 30s budget.
            setupResults.ShouldAllBe(r =>
                r.Status == BrowseStatus.Success || r.Status == BrowseStatus.Partial);

            // Snapshot to assign element refs
            var snapshotTasks = sessions.Select(sid =>
                fixture.Browser.SnapshotAsync(new SnapshotRequest(SessionId: sid))).ToList();
            var snapshots = await Task.WhenAll(snapshotTasks);

            snapshots.ShouldAllBe(s => s.RefCount > 0);

            // Execute hover actions in parallel on e-1 (lightweight page, fast element resolution)
            var actionTasks = sessions.Select(sid =>
                fixture.Browser.ActionAsync(new WebActionRequest(
                    SessionId: sid,
                    Ref: "e-1",
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

    // The agent reported four times that it could not read a reddit thread's comments "because of
    // cookie consent". The consent wall was real but incidental: the comments were in the DOM the
    // whole time, and dismissal worked. What actually happened is that readability discarded the
    // <shreddit-comment> custom elements as non-content and settled on the consent notice -- the
    // only ordinary prose left -- returning 625 characters of cookie text as the article. This is
    // the end-to-end proof, on the real page, that a readability browse now reaches the comments.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task NavigateAsync_RedditThreadWithReadability_ReturnsCommentsNotTheConsentNotice()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Reddit serves this thread as a progressive render, and which document a given
            // navigation ends up extracting from is a coin flip the browse does not control: the
            // same session, navigated twice in a row, produced 735 characters of consent page and
            // then 23,424 characters of thread. What makes that a retry rather than a papering-over
            // is the check below — the comments are asked for in the DOM, and a second attempt is
            // spent only when they are demonstrably there and readability still did not reach them.
            // A page with no comments on it never retries and fails as it always did, which is the
            // regression this case exists for.
            var result = await BrowseTheThreadAsync(sessionId);
            if (result.ContentLength < 5000 && await CommentsAreOnThePageAsync(sessionId))
            {
                result = await BrowseTheThreadAsync(sessionId);
            }

            testOutputHelper.WriteLine($"status={result.Status} contentLength={result.ContentLength}");

            // The subject here is what readability does with the page, not whether reddit is up.
            // Asserting Success made a rate-limited or slow fetch from a third party read as a
            // defect in the browse, which is how this failed a full-suite run that had nothing to
            // do with it. An unreachable page has nothing to say about the extraction, so skip.
            Skip.If(
                result.Status != BrowseStatus.Success,
                $"reddit did not serve the thread ({result.Status}: {result.ErrorMessage})");

            // Reddit answers this URL with one of three documents, and only one of them is this
            // test's subject. Which one arrives is decided by how hard the suite has been hitting
            // reddit, not by anything in the browse.
            //
            //   - The thread: 20-35k characters with the comments in it. What the case wants.
            //   - A bot challenge: ~134 characters, "Complete the challenge below and let us know
            //     you're a real person". No comments and no notice — it says nothing about the
            //     extractor either way.
            //   - A consent page: ~735 characters that are entirely cookie prose. This one IS the
            //     regression: readability discarded the <shreddit-comment> elements as non-content
            //     and settled on the only ordinary prose on the page.
            //
            // The consent page is server-rendered — the wall is the whole document rather than an
            // overlay over the comments — which is why DismissedModals comes back empty on it and
            // why widening ModalDismisser's detection window would not change the outcome. There is
            // nothing on the page to dismiss.
            //
            // Recognised by what each says rather than by how short it is, because two of the three
            // are short and they want opposite treatment.
            var content = result.Content ?? "";
            var looksLikeConsent = content.Contains("cookie", StringComparison.OrdinalIgnoreCase);
            var challenged = content.Contains("not for bots", StringComparison.OrdinalIgnoreCase)
                || content.Contains("a real person", StringComparison.OrdinalIgnoreCase);
            Skip.If(
                challenged,
                $"reddit served its bot challenge rather than the thread ({content.Length} chars).");
            Skip.If(
                content.Length < 1000
                    && !looksLikeConsent
                    && content.Contains("rdt=", StringComparison.OrdinalIgnoreCase),
                $"reddit served its throttling interstitial rather than the thread ({content.Length} chars).");

            // Deliberately NOT skipped on the consent page: that is the regression this test exists
            // for, and skipping it would retire the case without saying so.
            content.Length.ShouldBeGreaterThan(
                5000,
                looksLikeConsent
                    ? $"readability settled on reddit's consent page again ({content.Length} chars) "
                      + "instead of the comments."
                    : $"the body came back at {content.Length} chars with no comments in it.");
            content.ShouldContain("Comments", Case.Insensitive);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }


    private Task<BrowseResult> BrowseTheThreadAsync(string sessionId) =>
        fixture.Browser.NavigateAsync(new BrowseRequest(
            SessionId: sessionId,
            Url: "https://www.reddit.com/r/anime/comments/wbj3yc/can_someone_recommend_a_hidden_gem_isekai_pls/",
            MaxLength: 20000,
            UseReadability: true));

    // The comments are <shreddit-comment> custom elements, which is exactly what readability used
    // to discard as non-content. Asking the live DOM separates "reddit did not send the thread"
    // from "the thread is here and the extractor did not reach it".
    private async Task<bool> CommentsAreOnThePageAsync(string sessionId) =>
        await fixture.Browser.EvaluateOnSessionAsync<int>(
            sessionId, "() => document.querySelectorAll('shreddit-comment').length") > 0;

}