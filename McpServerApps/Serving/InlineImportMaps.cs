using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;

namespace McpServerApps.Serving;

// A page's inline import maps, hashed for its policy: the browser holds an import map to script-src
// like any inline script, and a built app maps its modules inline (pinned by `./publish`). Each map's
// text is hashed as the browser reads it — the HTML parser makes every line end LF, as AngleSharp's
// does — so exactly these maps run and no other inline script.
public static class InlineImportMaps
{
    private static readonly HtmlParser _parser = new();

    public static IReadOnlyList<string> Hashes(string html)
    {
        using var document = _parser.ParseDocument(html);
        return
        [
            .. document.QuerySelectorAll("script[type=importmap]:not([src])")
                .Select(map => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(map.TextContent))))
                .Distinct(StringComparer.Ordinal)
        ];
    }
}