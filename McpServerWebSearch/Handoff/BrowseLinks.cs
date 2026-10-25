using System.Security.Cryptography;
using McpServerWebSearch.Settings;

namespace McpServerWebSearch.Handoff;

// What the handoff's links and the logins page's share: where a link points, and the key a claimed
// page is bound to.
internal static class BrowseLinks
{
    public static string For(HandoffConfiguration settings, string page, string token) =>
        $"{settings.PublicBaseUrl.TrimEnd('/')}/browse/{page}/{token}";

    public static string NewViewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}