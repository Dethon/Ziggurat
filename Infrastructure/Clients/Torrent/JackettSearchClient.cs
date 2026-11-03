using System.Net;
using System.Web;
using System.Xml.Linq;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients.Torrent;

public class JackettSearchClient(HttpClient client, string apiKey, TimeSpan? searchDeadline = null) : ISearchClient
{
    private readonly XNamespace _torznabNs = "http://torznab.com/schemas/2015/feed";
    private readonly TimeSpan _searchDeadline = searchDeadline ?? TimeSpan.FromSeconds(10);

    public async Task<SearchResult[]> Search(string query, CancellationToken cancellationToken = default)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(_searchDeadline);
        var (indexers, listingFailure) = await GetIndexers(deadlineCts.Token);
        var answers = await Task.WhenAll(indexers.Select(x => QueryIndexer(x, query, deadlineCts.Token)));

        // One indexer down is the others' results, and indexers still running at the deadline are a
        // search that ran out of time with what arrived. No indexer answering while something failed
        // - Jackett unreachable, every indexer erroring - is no search at all, and "nothing found"
        // would tell the person there is nothing to download when nothing was searched.
        if (!answers.Any(a => a.Answered)
            && (listingFailure ?? answers.Select(a => a.Failure).FirstOrDefault(f => f is not null)) is { } failure)
        {
            throw new HttpRequestException(
                $"Jackett did not answer the search: {failure}", null, HttpStatusCode.ServiceUnavailable);
        }

        return answers.SelectMany(a => a.Results).ToArray();
    }

    // Answered: the indexer gave a response, whatever it held. Neither answered nor failed: still
    // running at the deadline.
    private sealed record IndexerAnswer(SearchResult[] Results, bool Answered, string? Failure = null);

    // The configured indexers, or "all" when they cannot be listed - and why not, when the listing
    // failed rather than ran out of time.
    private async Task<(string[] Indexers, string? Failure)> GetIndexers(CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = $"indexers/all/results/torznab/api?apikey={apiKey}&t=indexers&configured=true";
            var response = await client.GetStringAsync(requestUri, cancellationToken);
            var xmlDoc = XDocument.Parse(response);

            var indexerIds = xmlDoc.Descendants("indexer")
                .Select(x => x.Attribute("id")?.Value ?? x.Element("id")?.Value)
                .Where(x => x is not null)
                .Cast<string>()
                .ToArray();

            return (indexerIds.Length > 0 ? indexerIds : ["all"], null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (["all"], null);
        }
        catch (Exception ex)
        {
            return (["all"], ex.Message);
        }
    }

    private async Task<IndexerAnswer> QueryIndexer(
        string indexer, string searchQuery, CancellationToken cancellationToken)
    {
        try
        {
            var encodedQuery = HttpUtility.UrlEncode(searchQuery);
            var requestUri = $"indexers/{indexer}/results/torznab/api?apikey={apiKey}&t=search&q={encodedQuery}";
            var response = await client.GetAsync(requestUri, cancellationToken);

            return !response.IsSuccessStatusCode
                ? new IndexerAnswer([], false, $"indexer {indexer} answered {(int)response.StatusCode}")
                : new IndexerAnswer(ParseTorznabResponse(await response.Content.ReadAsStringAsync(cancellationToken)), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new IndexerAnswer([], false);
        }
        catch (Exception ex)
        {
            return new IndexerAnswer([], false, ex.Message);
        }
    }

    private SearchResult[] ParseTorznabResponse(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return [];
        }

        try
        {
            var xmlDoc = XDocument.Parse(xmlContent);
            var items = xmlDoc.Descendants("item").ToArray();
            return TrimAndMapResults(items, ParseTorznabItem);
        }
        catch
        {
            return [];
        }
    }

    private SearchResult? ParseTorznabItem(XElement item)
    {
        try
        {
            var titleElement = item.Element("title");
            var linkElement = item.Element("link");
            var sizeElement = item.Element("size");
            var torznabAttributes = item.Elements(_torznabNs + "attr")
                .GroupBy(
                    attr => attr.Attribute("name")?.Value ?? string.Empty,
                    attr => attr.Attribute("value")?.Value)
                .ToDictionary(x => x.Key, x => string.Join("/", x));

            var link = torznabAttributes.GetValueOrDefault("magneturl") ?? linkElement?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(link))
            {
                return null;
            }

            var title = titleElement?.Value ?? string.Empty;
            var category = torznabAttributes.GetValueOrDefault("category") ?? string.Empty;
            long? size = null;
            int? seeders = null;
            int? peers = null;
            if (long.TryParse(sizeElement?.Value, out var s))
            {
                size = s;
            }

            if (int.TryParse(torznabAttributes.GetValueOrDefault("seeders"), out var sd))
            {
                seeders = sd;
            }

            if (int.TryParse(torznabAttributes.GetValueOrDefault("peers"), out var p))
            {
                peers = p;
            }

            return new SearchResult
            {
                Title = title,
                Category = category,
                Id = link.GetHashCode(),
                Link = link,
                Size = size,
                Seeders = seeders,
                Peers = peers
            };
        }
        catch
        {
            return null;
        }
    }

    private static SearchResult[] TrimAndMapResults<T>(
        IEnumerable<T> sourceItems, Func<T, SearchResult?> parser, int maxResults = 10)
    {
        return sourceItems
            .Select(parser)
            .Where(x => x is not null && !string.IsNullOrEmpty(x.Link) && x.Seeders.GetValueOrDefault(0) > 1)
            .Cast<SearchResult>()
            .OrderByDescending(x => x.Seeders.GetValueOrDefault(0))
            .Take(maxResults)
            .ToArray();
    }
}