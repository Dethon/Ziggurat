using System.Diagnostics;
using System.Net;
using Infrastructure.Clients.Torrent;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class JackettSearchClientTests
{
    private const string IndexersXml =
        """
        <indexers>
          <indexer id="fast" configured="true"></indexer>
          <indexer id="slow" configured="true"></indexer>
        </indexers>
        """;

    private static string TorznabResponse(string title) =>
        $"""
         <rss xmlns:torznab="http://torznab.com/schemas/2015/feed">
           <channel>
             <item>
               <title>{title}</title>
               <link>magnet:?xt=urn:btih:{title}</link>
               <size>1000</size>
               <torznab:attr name="seeders" value="5" />
               <torznab:attr name="peers" value="10" />
             </item>
           </channel>
         </rss>
         """;

    private sealed class RoutingHandler(TimeSpan slowIndexerDelay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("t=indexers"))
            {
                return Ok(IndexersXml);
            }

            if (uri.Contains("/indexers/slow/"))
            {
                await Task.Delay(slowIndexerDelay, cancellationToken);
                return Ok(TorznabResponse("slow-result"));
            }

            return Ok(TorznabResponse("fast-result"));
        }

        private static HttpResponseMessage Ok(string content) =>
            new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }

    private static JackettSearchClient CreateClient(RoutingHandler handler, TimeSpan searchDeadline)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return new JackettSearchClient(httpClient, "apikey", searchDeadline);
    }

    [Fact]
    public async Task Search_IndexerExceedsDeadline_ReturnsAccumulatedResultsWithoutWaiting()
    {
        var handler = new RoutingHandler(slowIndexerDelay: TimeSpan.FromSeconds(8));
        var client = CreateClient(handler, searchDeadline: TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        var results = await client.Search("ubuntu", CancellationToken.None);
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4));
        results.ShouldHaveSingleItem().Title.ShouldBe("fast-result");
    }

    [Fact]
    public async Task Search_AllIndexersWithinDeadline_ReturnsResultsFromAllIndexers()
    {
        var handler = new RoutingHandler(slowIndexerDelay: TimeSpan.Zero);
        var client = CreateClient(handler, searchDeadline: TimeSpan.FromSeconds(10));

        var results = await client.Search("ubuntu", CancellationToken.None);

        results.Length.ShouldBe(2);
        results.Select(x => x.Title).ShouldBe(["fast-result", "slow-result"], ignoreOrder: true);
    }

    private sealed class FailingHandler(Func<string, HttpResponseMessage?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            answer(request.RequestUri!.ToString()) is { } response
                ? Task.FromResult(response)
                : Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Name or service not known (jackett:9117)"));
    }

    private static JackettSearchClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, "apikey", TimeSpan.FromSeconds(10));

    // Jackett not answering is not a search that found nothing: told "0 results", the agent tells the
    // person there is nothing to download when nothing was searched at all.
    [Fact]
    public async Task Search_JackettIsNotAnswering_Throws()
    {
        var client = CreateClient(new FailingHandler(_ => null));

        await Should.ThrowAsync<HttpRequestException>(() => client.Search("ubuntu", CancellationToken.None));
    }

    [Fact]
    public async Task Search_EveryIndexerFails_Throws()
    {
        var client = CreateClient(new FailingHandler(uri => uri.Contains("t=indexers")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(IndexersXml) }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Should.ThrowAsync<HttpRequestException>(() => client.Search("ubuntu", CancellationToken.None));
    }

    // One indexer down is the others' results, as it always was.
    [Fact]
    public async Task Search_OneIndexerFails_ReturnsTheOthers()
    {
        var client = CreateClient(new FailingHandler(uri =>
            uri.Contains("t=indexers") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(IndexersXml) }
            : uri.Contains("/indexers/slow/") ? null
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TorznabResponse("fast-result")) }));

        var results = await client.Search("ubuntu", CancellationToken.None);

        results.ShouldHaveSingleItem().Title.ShouldBe("fast-result");
    }

    // An indexer that answers with nothing found is an answer.
    [Fact]
    public async Task Search_EveryIndexerAnswersEmpty_ReturnsNothing()
    {
        var client = CreateClient(new FailingHandler(uri => uri.Contains("t=indexers")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(IndexersXml) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<rss><channel></channel></rss>") }));

        (await client.Search("ubuntu", CancellationToken.None)).ShouldBeEmpty();
    }

    // Unreachable can cost the whole deadline: a name that does not resolve took the listing's time,
    // and the fallback query was then cut off by the deadline - which on its own reads as slow.
    [Fact]
    public async Task Search_TheListingFailsAndTheFallbackRunsOutOfTime_Throws()
    {
        var handler = new SlowFallbackHandler();
        var client = new JackettSearchClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, "apikey", TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<HttpRequestException>(() => client.Search("ubuntu", CancellationToken.None));
    }

    private sealed class SlowFallbackHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString().Contains("t=indexers"))
            {
                throw new HttpRequestException("Name or service not known (jackett:9117)");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}