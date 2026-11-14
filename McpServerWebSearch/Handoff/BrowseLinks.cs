using System.Security.Cryptography;
using Domain.DTOs.Tunnel;
using McpServerWebSearch.Settings;

namespace McpServerWebSearch.Handoff;

// What the handoff's links and the logins page's share: where a link points — under the household of
// the account it was minted for, whose web search this is — and the key a claimed page is bound to.
internal static class BrowseLinks
{
    public const string Handoff = "handoff";
    public const string Logins = "logins";

    public static string For(HandoffConfiguration settings, string household, string page, string token) =>
        $"{settings.PublicBaseUrl.TrimEnd('/')}{BrowsePagePath.For(household)}/{page}/{token}";

    public static string NewViewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}