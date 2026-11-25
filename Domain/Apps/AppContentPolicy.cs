namespace Domain.Apps;

// The Content-Security-Policy every answer on a built app's origin carries. Code, styles and fonts come
// from the app itself or the fixed CDN list (a setting of the apps server); the outside origins its
// manifest names under `connect` may be fetched and shown as images, never run. Inline styles are
// allowed (a framework's style attributes); inline script never is, which is why the server's own
// additions to a page are files on the origin — save a page's own inline import maps, which the
// browser holds to script-src too and which run by their hashes alone, per page. The manifest and
// the service worker the server generates are the origin's own.
public static class AppContentPolicy
{
    // An answer that is no app's: an unknown label, someone not let in, a removed app's page.
    public const string Closed = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public static string For(AppManifest manifest, IReadOnlyList<string> cdns, IReadOnlyList<string>? scriptHashes = null)
    {
        string Sources(params IEnumerable<string>[] parts) => string.Join(' ', parts.SelectMany(p => p));
        string[] self = ["'self'"];

        return string.Join("; ",
            "default-src 'none'",
            $"script-src {Sources(self, (scriptHashes ?? []).Select(hash => $"'{hash}'"), cdns)}",
            $"style-src {Sources(self, ["'unsafe-inline'"], cdns)}",
            $"font-src {Sources(self, cdns)}",
            $"img-src {Sources(self, ["data:", "blob:"], cdns, manifest.Connect)}",
            $"connect-src {Sources(self, manifest.Connect)}",
            "manifest-src 'self'",
            "worker-src 'self'",
            "frame-ancestors 'none'",
            "base-uri 'none'",
            "form-action 'none'");
    }
}