using System.Text.Json;
using AngleSharp.Html.Parser;
using Domain.Apps;

namespace McpServerApps.Publishing;

// What `./publish` holds a built app's HTML to: every script, module preload and stylesheet loaded
// from outside the app names an exact version in its address and carries an `integrity` hash, and an
// import map names an integrity entry for every outside module it maps. The CDN may serve anything
// at an address later; the hash makes the browser refuse bytes the agent never saw. Every problem is
// named, so one publish shows the agent all of them.
public static class OutsideCodePinning
{
    private static readonly HtmlParser _parser = new();

    public static IReadOnlyList<string> Problems(string file, string html) => Check(file, html).Problems;

    // What a page loads from outside, judged once: every problem, and every address that passed — the
    // pinned code its policy lets it load (AppContentPolicy), so what is served is what was checked here.
    public static PinningOutcome Check(string file, string html)
    {
        using var document = _parser.ParseDocument(html);

        var loads = document.QuerySelectorAll("script[src]")
            .Select(e => (Kind: "script", Address: e.GetAttribute("src")!, Integrity: e.GetAttribute("integrity")))
            .Concat(document.QuerySelectorAll("link[href]")
                .Select(e => (Rels: Rels(e.GetAttribute("rel")), Element: e))
                .Where(link => link.Rels.Overlaps(["stylesheet", "modulepreload"]))
                .Select(link => (Kind: link.Rels.Contains("stylesheet") ? Stylesheet : "module preload",
                    Address: link.Element.GetAttribute("href")!, Integrity: link.Element.GetAttribute("integrity"))))
            .Where(load => IsOutside(load.Address))
            .ToList();

        var loadProblems = loads.SelectMany(load => (string?[])
        [
            PinnedCode.HasExactVersion(load.Address)
                ? null
                : $"{file}: the {load.Kind} {load.Address} names no exact version: pin one in the address (name@1.2.3).",
            string.IsNullOrWhiteSpace(load.Integrity)
                ? $"{file}: the {load.Kind} {load.Address} has no integrity attribute: add integrity=\"sha384-…\" for those exact bytes."
                : null
        ]).OfType<string>();
        var pinned = loads.Where(load => PinnedCode.HasExactVersion(load.Address) && !string.IsNullOrWhiteSpace(load.Integrity)).ToList();

        var maps = document.QuerySelectorAll("script[type=importmap]").Select(map => ImportMap(file, map.TextContent)).ToList();

        return new PinningOutcome(
            [.. loadProblems.Concat(maps.SelectMany(m => m.Problems))],
            new PinnedCode
            {
                Scripts = [.. pinned.Where(l => l.Kind != Stylesheet).Select(l => l.Address.Trim()).Concat(maps.SelectMany(m => m.Pinned)).Distinct()],
                Styles = [.. pinned.Where(l => l.Kind == Stylesheet).Select(l => l.Address.Trim()).Distinct()]
            });
    }

    // Every page under a served root, by its path from there (a page outside it is the agent's source,
    // never loaded by a person), judged together: what publish refuses, and what the root's policy loads.
    public static async Task<PinningOutcome> CheckRootAsync(string root, CancellationToken ct)
    {
        var pages = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".html" or ".htm" && new FileInfo(file).LinkTarget is null)
            .Order(StringComparer.Ordinal);
        var outcomes = await Task.WhenAll(pages.Select(async page =>
            Check(Path.GetRelativePath(root, page).Replace('\\', '/'), await File.ReadAllTextAsync(page, ct))));
        return new PinningOutcome(
            [.. outcomes.SelectMany(o => o.Problems)],
            new PinnedCode
            {
                Scripts = [.. outcomes.SelectMany(o => o.Pinned.Scripts).Distinct()],
                Styles = [.. outcomes.SelectMany(o => o.Pinned.Styles).Distinct()]
            });
    }

    private const string Stylesheet = "stylesheet";

    private static (IReadOnlyList<string> Problems, IReadOnlyList<string> Pinned) ImportMap(string file, string json)
    {
        JsonDocument map;
        try
        {
            map = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return ([$"{file}: an import map is not JSON: {ex.Message}"], []);
        }

        using (map)
        {
            var root = map.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ([$"{file}: an import map must be a JSON object."], []);
            }

            var integrity = Members(root, "integrity").Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            var outside = Mapped(root.TryGetProperty("imports", out var imports) ? imports : default)
                .Concat(Members(root, "scopes").SelectMany(scope => Mapped(scope.Value)))
                .Where(m => IsOutside(m.Module))
                .DistinctBy(m => m.Module, StringComparer.Ordinal)
                .ToList();
            // A prefix (a specifier ending in /) lets every module under it load, and no one integrity
            // entry can name them all.
            return (
            [
                .. outside.Where(m => m.Specifier.EndsWith('/'))
                    .Select(m => $"{file}: the import map maps the prefix {m.Specifier} to the outside {m.Module}, which no "
                                 + "integrity entry can cover: map each module by its full address instead."),
                .. outside.Where(m => !m.Specifier.EndsWith('/') && !integrity.Contains(m.Module))
                    .Select(m => $"{file}: the import map names the outside module {m.Module} with no integrity entry: "
                                 + "map each module by its full address and add its hash under \"integrity\".")
            ], [.. outside.Where(m => !m.Specifier.EndsWith('/') && integrity.Contains(m.Module)).Select(m => m.Module)]);
        }
    }

    private static IEnumerable<(string Specifier, string Module)> Mapped(JsonElement mapping) =>
        mapping.ValueKind == JsonValueKind.Object
            ? mapping.EnumerateObject().Where(m => m.Value.ValueKind == JsonValueKind.String).Select(m => (m.Name, m.Value.GetString()!))
            : [];

    private static IEnumerable<JsonProperty> Members(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value.EnumerateObject() : [];

    private static HashSet<string> Rels(string? rel) =>
        new((rel ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    // Absolute, or protocol-relative: anything the browser fetches from another origin.
    private static bool IsOutside(string address)
    {
        var trimmed = address.Trim();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
    }
}

// A page's (or a root's) judgment: what refuses the publish, and the outside code that passed.
public sealed record PinningOutcome(IReadOnlyList<string> Problems, PinnedCode Pinned);