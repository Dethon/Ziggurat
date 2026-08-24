using Domain.Tools.MusicAssistant;
using JetBrains.Annotations;

namespace Domain.Contracts;

// Music Assistant's own API, reached directly rather than through Home Assistant.
//
// Home Assistant cannot express either of these calls. `media_player.browse_media` has no podcast
// branch (it raises BrowseError for a podcast URI), `media_player.search_media` and
// `music_assistant.search` never return episodes, and `music_assistant.get_library` only lists items
// the user saved. So the episode listing a caller needs to play one specific episode exists ONLY on
// MA's native interface.
public interface IMusicAssistantClient
{
    Task<IReadOnlyList<MaMediaItem>> SearchAsync(
        string query, string mediaType, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<MaMediaItem>> GetPodcastEpisodesAsync(MaUri podcast, CancellationToken ct = default);

    // Home Assistant's media_position is refreshed only by a state transition, so a player that has
    // been going for a while still reports the position it started from. Music Assistant's queue
    // keeps the real one. Returns null when the queue is unknown or has nothing loaded.
    Task<MaQueuePosition?> GetQueuePositionAsync(string queueId, CancellationToken ct = default);
}

// What Music Assistant knows about where a queue is. `ElapsedTime` is live while the queue plays,
// but MA can also report a value it has not caught up to yet — right after a resume it repeats the
// previous number beside a fresh `LastUpdated`. So the pair is carried verbatim and the decision
// about whether it can be trusted belongs to the reader, not to this record.
[PublicAPI]
public record MaQueuePosition
{
    public required double ElapsedTime { get; init; }
    public required DateTimeOffset LastUpdated { get; init; }
    public string? State { get; init; }
}

[PublicAPI]
public record MaMediaItem
{
    public required string Name { get; init; }

    // The value `music_assistant.play_media` accepts verbatim as `media_id`. Everything else about
    // an item is descriptive; this is the only field that makes it playable.
    public required string Uri { get; init; }
    public double? DurationSeconds { get; init; }
}