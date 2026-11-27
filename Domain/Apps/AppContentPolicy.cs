namespace Domain.Apps;

// The Content-Security-Policy every answer on a built app's origin carries. Code, styles and fonts come
// from the app itself or, on the fixed CDN list (a setting of the apps server), from exactly the packages
// at the versions its pages were found pinned with (PinnedCode), never a CDN host whole: what was checked
// is what runs, so a module's `import()` of anything else on the same CDN is refused. A redirect relaxes a
// source's path in the browser (the target is matched on its host alone), so a pinned address on a CDN
// that redirects could still land elsewhere on that CDN; the CDNs answer a pinned version's files
// directly, and a pinned load's integrity still holds its own bytes. Images may come from a CDN host
// whole (they run nothing). The outside origins the manifest names under `connect` may be fetched and
// shown as images, never run. Inline styles are allowed (a framework's style attributes); inline script
// never is, which is why the server's own additions to a page are files on the origin — save a page's own
// inline import maps, which the browser holds to script-src too and which run by their hashes alone, per
// page. The manifest and the service worker the server generates are the origin's own.
public static class AppContentPolicy
{
    // An answer that is no app's: an unknown label, someone not let in, a removed app's page.
    public const string Closed = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public static string For(
        AppManifest manifest, IReadOnlyList<string> cdns, PinnedCode? pinned = null, IReadOnlyList<string>? scriptHashes = null)
    {
        string Sources(params IEnumerable<string>[] parts) => string.Join(' ', parts.SelectMany(p => p).Distinct(StringComparer.Ordinal));
        IEnumerable<string> Pinned(IEnumerable<string> addresses) => addresses.SelectMany(address => PinnedCode.Sources(address, cdns));
        string[] self = ["'self'"];
        var code = pinned ?? PinnedCode.None;

        return string.Join("; ",
            "default-src 'none'",
            $"script-src {Sources(self, (scriptHashes ?? []).Select(hash => $"'{hash}'"), Pinned(code.Scripts))}",
            $"style-src {Sources(self, ["'unsafe-inline'"], Pinned(code.Styles))}",
            $"font-src {Sources(self, Pinned(code.Styles))}",
            $"img-src {Sources(self, ["data:", "blob:"], cdns, manifest.Connect)}",
            $"connect-src {Sources(self, manifest.Connect)}",
            "manifest-src 'self'",
            "worker-src 'self'",
            "frame-ancestors 'none'",
            "base-uri 'none'",
            "form-action 'none'");
    }
}