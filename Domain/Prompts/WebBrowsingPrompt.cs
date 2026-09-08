namespace Domain.Prompts;

public static class WebBrowsingPrompt
{
    // Named rather than "system_prompt": the manifest keys a budget and a place on this name, and
    // two servers answering to the same generic one cannot both be declared.
    public const string Name = "websearch_prompt";

    public const string Description =
        "Web research and browsing: search, navigation and reading a page as markdown";

    public const string AgentSystemPrompt =
        """
        ### Web browsing

        You have access to a persistent browser session that maintains state across multiple page
        interactions: web_search, web_browse, web_snapshot, web_action and view_image. Before any
        web task — a search, reading a page, looking at its pictures, filling a form, going back —
        load the `web-browsing` skill, which carries the workflow, the principles, the error
        recovery and how to answer; each tool's own description carries its arguments.

        - **Start with search.** Use web_search to find URLs rather than guessing them.
        - **No probe calls.** The tools work; never spend a call checking that they do — no
          throwaway search, no minimal fetch of a placeholder page. The first web call of a turn
          is already part of the task, or the turn makes none.
        """;

    // The choosing rules' claims: what is decided before the first web call, and so before any
    // skill is loaded. The doing rules are claims of the web-browsing skill.
    public static readonly PromptClaim UrlComesFromASearch =
        new("web.url-comes-from-a-search",
            "A page is reached by searching for it rather than by guessing its url.");

    public static readonly PromptClaim NoProbeCalls =
        new("web.no-probe-calls",
            "A web tool is called only in service of the task — never a throwaway search or minimal fetch to check that it works.");

    public static readonly IReadOnlyList<PromptClaim> Claims = [UrlComesFromASearch, NoProbeCalls];
}