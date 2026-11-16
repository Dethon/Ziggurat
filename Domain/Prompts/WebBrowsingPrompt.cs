namespace Domain.Prompts;

// The browser's standing prose (Sections/websearch_prompt.md), served by mcp-websearch.
public static class WebBrowsingPrompt
{
    // Named rather than "system_prompt": the manifest keys a budget and a place on this name, and
    // two servers answering to the same generic one cannot both be declared.
    public const string Name = "websearch_prompt";

    public const string Description =
        "Web research and browsing: search, navigation and reading a page as markdown";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    public static readonly string AgentSystemPrompt = _document.Text;

    public static readonly PromptClaim UrlComesFromASearch =
        _document.Claim("web.url-comes-from-a-search");

    public static readonly PromptClaim NoProbeCalls =
        _document.Claim("web.no-probe-calls");
}