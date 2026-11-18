using System.Text.RegularExpressions;

namespace McpServerSugary.Glucose;

// Where each LibreLinkUp region is served. A login at the wrong one is redirected by region code, so
// the code comes off the wire: one that is not a bare code places nothing, or an answer could send her
// password to whatever host it named.
public sealed partial class LibreLinkUpRegions(
    string global, string regional, IReadOnlyDictionary<string, string>? exceptions = null)
{
    public static readonly LibreLinkUpRegions Abbott = new(
        "https://api.libreview.io/",
        "https://api-{region}.libreview.io/",
        new Dictionary<string, string> { ["ru"] = "https://api.libreview.ru/" });

    // Null for the global address, which redirects an account to its own region.
    public Uri? BaseFor(string? region)
    {
        if (Normalize(region) is not { } code)
        {
            return string.IsNullOrWhiteSpace(region) ? new Uri(global) : null;
        }

        return new Uri(exceptions?.GetValueOrDefault(code) ?? regional.Replace("{region}", code));
    }

    // A region as the client keeps it: lowercase, null for the global address or anything not a code.
    public static string? Normalize(string? region) =>
        region?.Trim().ToLowerInvariant() is { } code && RegionCode().IsMatch(code) ? code : null;

    [GeneratedRegex("^[a-z]{2,3}[0-9]?$")]
    private static partial Regex RegionCode();
}