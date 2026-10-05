using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

// Action files the VFS serves itself instead of forwarding to Home Assistant.
//
// `music_assistant.podcast_episodes` has no Home Assistant service behind it because none exists:
// `browse_media` raises BrowseError for a podcast URI, and neither `search_media`,
// `music_assistant.search` nor `music_assistant.get_library` ever returns an episode. Without this
// action a caller cannot obtain an episode's URI, and an episode is playable ONLY by its URI — a
// title passed to `music_assistant.play_media` resolves to the show and starts its newest episode.
// It is declared as a normal HaServiceDefinition so glob, --help and exec routing treat it like any
// other action; HaFileSystem.ExecAsync intercepts it before the Home Assistant call.
public static class HaMusicActions
{
    public const string MusicAssistantDomain = "music_assistant";
    public const string PodcastEpisodesService = "podcast_episodes";

    public const int DefaultEpisodeLimit = 25;

    public static HaServiceDefinition PodcastEpisodes { get; } = new()
    {
        Domain = MusicAssistantDomain,
        Service = PodcastEpisodesService,
        Description =
            "List a podcast's episodes with the exact URI each one plays by. "
            + "Pass an episode's uri to ./music_assistant.play_media --media_id to play it.",
        // Names the media_player class so HaActionResolver's cross-domain rule gives it a slot in
        // every player directory, exactly like music_assistant.play_media.
        Target = JsonNode.Parse("""{"entity":[{"domain":["media_player"]}]}"""),
        Fields = new Dictionary<string, HaServiceField>
        {
            ["podcast"] = new()
            {
                Required = true,
                Description = "The show's name, or its Music Assistant uri."
            },
            ["match"] = new()
            {
                Description = "Keep only episodes whose title contains this text (ignores case and accents)."
            },
            ["limit"] = new()
            {
                Description = $"Maximum episodes to return (default {DefaultEpisodeLimit}).",
                Selector = JsonNode.Parse("""{"number":{"min":1,"max":200}}""")
            }
        }
    };

    public static bool IsPodcastEpisodes(HaServiceDefinition svc) =>
        svc.Domain.Equals(MusicAssistantDomain, StringComparison.Ordinal)
        && svc.Service.Equals(PodcastEpisodesService, StringComparison.Ordinal);
}