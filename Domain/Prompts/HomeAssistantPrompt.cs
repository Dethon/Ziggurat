namespace Domain.Prompts;

// The house's standing prose (Sections/home_assistant_guide.md), served by mcp-homeassistant.
public static class HomeAssistantPrompt
{
    public const string Name = "home_assistant_guide";

    public const string Description =
        "Guide for controlling Home Assistant devices via the /ha virtual filesystem";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    public static readonly string SystemPrompt = _document.Text;

    public static readonly PromptClaim ExactlyWhatWasAsked =
        _document.Claim("home.exactly-what-was-asked");

    public static readonly PromptClaim AreaSlugIsReadNotDerived =
        _document.Claim("home.area-slug-is-read-not-derived");

    public static readonly PromptClaim RoomRequestUsesTheRoomAction =
        _document.Claim("home.room-request-uses-the-room-action");

    public static readonly PromptClaim ReactingToTheHomeIsAWatch =
        _document.Claim("home.reacting-to-the-home-is-a-watch");
}