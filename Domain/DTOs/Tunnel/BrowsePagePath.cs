namespace Domain.DTOs.Tunnel;

// Where a browse link points: /browse/<household>/<page>/..., the household whose web search serves
// it first. Web search mints it for the household of the account the call acts for; the relay
// forwards every exchange under it down that household's tunnel; and the page's cookie is scoped to
// it, so no household's server is ever sent another's view key. Spelled once, for both ends.
public static class BrowsePagePath
{
    public const string Root = "/browse";

    // The pages a link sat directly under Root with before links named a household. A household id is
    // 32 lowercase hex characters, so none is ever called this.
    private static readonly string[] _pagesBeforeHouseholds = ["handoff", "logins"];

    public static string For(string household) => $"{Root}/{household}";

    // The household a path under Root names, or null for a link minted before links named one — the
    // home's, as every link then was — or a path naming nothing.
    public static string? HouseholdOf(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries) is [_, var first, ..]
        && !_pagesBeforeHouseholds.Contains(first)
            ? first
            : null;
}