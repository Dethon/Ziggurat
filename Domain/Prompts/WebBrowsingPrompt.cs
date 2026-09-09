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

        Before you search the web, read a page, look at its pictures, fill or submit a form, or
        navigate a site, load the `web-browsing` skill: the tools you have, the workflow, the
        principles, the error recovery and how to answer are there. Every web task loads it, a
        plain search included — there is no web call small enough to skip it, and knowing which
        tool you would reach for is not the same as knowing how this browser is driven.

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