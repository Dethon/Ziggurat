using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Space;

public sealed record SpaceState
{
    public string CurrentSlug { get; init; } = "default";
    public string SpaceName { get; init; } = "Main";
    public string AccentColor { get; init; } = SpaceConfig.DefaultAccentColor;

    // The agent a conversation in this space opens on unless the person picked another here, as the
    // household's configuration names it; none when it names none.
    public string? DefaultAgent { get; init; }

    public static SpaceState Initial => new();
}