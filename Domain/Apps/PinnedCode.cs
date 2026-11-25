using System.Text.RegularExpressions;

namespace Domain.Apps;

// The outside code a built app's pages were found pinned with — an exact version in the address and an
// integrity hash, as `./publish` holds them (McpServerApps/Publishing/OutsideCodePinning) — and so the only
// outside code its policy lets them load (AppContentPolicy). Recorded per commit at publish, so serving
// never parses a page again, and found from the working tree for the preview.
public sealed partial record PinnedCode
{
    public static readonly PinnedCode None = new();

    // Scripts, module preloads and the modules an import map names with an integrity entry.
    public IReadOnlyList<string> Scripts { get; init; } = [];

    public IReadOnlyList<string> Styles { get; init; } = [];

    // A whole version in an address, major.minor.patch with an optional pre-release or build, as a path
    // segment or after the package's @: never a range, a tag or a major alone.
    public static bool HasExactVersion(string address) => ExactVersion().IsMatch(address);

    // What of a CDN a pinned address lets a page load, as policy source expressions: its package's version
    // directory (`https://cdn/npm/name@1.2.3/`), so a bundle's own chunks, a stylesheet's fonts and an esm.sh
    // stub's build load while another package or version does not, and the address itself when the version
    // is its last segment; a query or fragment is no part of a source (the version directory already lets
    // every variant a CDN builds of that version load). None for an address off the CDNs, or one that is no
    // https or http URL a policy can name whole: a character that would end a source or a directive.
    public static IReadOnlyList<string> Sources(string address, IReadOnlyList<string> cdns)
    {
        // Protocol-relative is the page's own scheme: https behind the proxy.
        var trimmed = address.Trim();
        if (!Uri.TryCreate(trimmed.StartsWith("//", StringComparison.Ordinal) ? "https:" + trimmed : trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || !SourcePath().IsMatch(uri.AbsolutePath))
        {
            return [];
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!cdns.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var path = uri.AbsolutePath;
        var version = ExactVersion().Match(path);
        if (!version.Success)
        {
            return [origin + path];
        }

        var directory = path[..(version.Index + version.Length)] + "/";
        return path.StartsWith(directory, StringComparison.Ordinal) ? [origin + directory] : [origin + directory, origin + path];
    }

    [GeneratedRegex(@"[@/]v?\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?(?=[/?#]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersion();

    // A path a source expression can carry whole: no `;` or `,` (which end a directive or a source list), no
    // quote, no whitespace.
    [GeneratedRegex(@"^/[A-Za-z0-9\-._~!$&()*+=:@%/]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SourcePath();
}