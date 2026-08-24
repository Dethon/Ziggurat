using Domain.Contracts;
using Domain.Tools.MusicAssistant;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// In-memory IMusicAssistantClient. Episodes are keyed by podcast URI so a test can assert the
// action resolved a free-text name to the right show before listing.
public class FakeMusicAssistantClient : IMusicAssistantClient
{
    public List<MaMediaItem> Podcasts { get; init; } = [];
    public Dictionary<string, List<MaMediaItem>> EpisodesByPodcastUri { get; init; } = [];

    public (string Query, string MediaType, int Limit)? LastSearch { get; private set; }
    public MaUri? LastEpisodeLookup { get; private set; }
    public Exception? Fault { get; set; }

    public Task<IReadOnlyList<MaMediaItem>> SearchAsync(
        string query, string mediaType, int limit, CancellationToken ct = default)
    {
        LastSearch = (query, mediaType, limit);
        if (Fault is not null)
        {
            return Task.FromException<IReadOnlyList<MaMediaItem>>(Fault);
        }
        IReadOnlyList<MaMediaItem> hits = Podcasts
            .Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
        return Task.FromResult(hits);
    }

    public Task<IReadOnlyList<MaMediaItem>> GetPodcastEpisodesAsync(MaUri podcast, CancellationToken ct = default)
    {
        LastEpisodeLookup = podcast;
        if (Fault is not null)
        {
            return Task.FromException<IReadOnlyList<MaMediaItem>>(Fault);
        }
        IReadOnlyList<MaMediaItem> episodes = EpisodesByPodcastUri.TryGetValue(podcast.ToString(), out var found)
            ? found
            : [];
        return Task.FromResult(episodes);
    }

    public Dictionary<string, MaQueuePosition> QueuePositions { get; init; } = [];

    public string? LastQueueLookup { get; private set; }

    public Task<MaQueuePosition?> GetQueuePositionAsync(string queueId, CancellationToken ct = default)
    {
        LastQueueLookup = queueId;
        if (Fault is not null)
        {
            return Task.FromException<MaQueuePosition?>(Fault);
        }
        return Task.FromResult(QueuePositions.GetValueOrDefault(queueId));
    }

    public static MaQueuePosition Position(double elapsed, string state = "playing") => new()
    {
        ElapsedTime = elapsed,
        LastUpdated = DateTimeOffset.Parse("2026-05-23T09:24:02Z"),
        State = state
    };

    public static MaMediaItem Item(string name, string uri, double? duration = null) =>
        new() { Name = name, Uri = uri, DurationSeconds = duration };
}