using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Tunnel;
using Domain.DTOs.WebChat;

namespace Domain.Apps;

// A built app's app.json: what it is called and coloured, which directory of its repository is
// served, which mount points it may read and which outside origins it may fetch. Judged whole before
// anything is stored, every broken rule named in its own words, so the agent fixes them in one go
// and a manifest is never kept half-valid.
public sealed record AppManifest
{
    public const string FileName = "app.json";
    public const int NameLength = 60;

    // What a home screen shows under an icon; longer is cut off by the launcher.
    public const int ShortNameLength = 12;
    public const int DescriptionLength = 300;

    // Longer than any provider's or local box's model id.
    public const int ModelLength = 200;

    public required string Name { get; init; }
    public string? ShortName { get; init; }
    public required string AccentColor { get; init; }
    public required string Description { get; init; }

    // The directory served, relative to the repository root; empty is the root itself.
    public string Root { get; init; } = "";

    // Checked for shape alone: the apps server cannot know an agent's mounts, so one the building
    // agent lacks answers unmounted when the app reads it.
    public IReadOnlyList<string> Reads { get; init; } = [];
    public IReadOnlyList<string> Connect { get; init; } = [];

    // The SVG the app installs with, by the path its origin serves it at (/icon.svg); none is its accent
    // colour's drawing. Its shape is checked here, the file itself at publish (AppIcon).
    public string? Icon { get; init; }

    // The markdown file whose words answer the app's asks in place of the building agent's persona, by
    // its path from the repository's root: never served for it, so it may sit outside the served root.
    // None, and the app cannot ask. Its shape is checked here, the file itself at publish and at each ask
    // (AppAsks.MaxInstructionsBytes).
    public string? Instructions { get; init; }

    // The servers whose tools the app's asks may use, by the name each is configured under: a shared server
    // its `sharedServers` key (sugary, apps, …), a household's own server its own name (vault, …), never its
    // host and port. Checked for shape alone, as reads are: one the building agent lacks is simply not
    // offered. None, and an ask is offered no tools.
    public IReadOnlyList<string> Asks { get; init; } = [];

    // The model the app's asks are answered on, as the agent's patchable models name it (vendor/name, a
    // local box's lemonade/name). Checked for shape alone: the agent answers on its own model when this is
    // not one it may be patched to, as it does any patch. None, and asks run on the agent's own.
    public string? Model { get; init; }

    // Every field app.json takes; any other is refused.
    public static readonly IReadOnlyList<string> Fields =
        ["name", "shortName", "accentColor", "description", "root", "reads", "connect", "icon", "instructions", "asks", "model"];

    public static AppManifestParse Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new AppManifestParse.Refused([$"{FileName} is not JSON: {ex.Message}"]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new AppManifestParse.Refused([$"{FileName} must be a JSON object."]);
            }

            var problems = new List<string>();
            var root = document.RootElement;
            problems.AddRange(root.EnumerateObject()
                .Where(p => !Fields.Contains(p.Name, StringComparer.Ordinal))
                .Select(p => $"unknown field '{p.Name}': {FileName} takes {string.Join(", ", Fields)}"));

            var name = Text(root, "name", problems);
            var shortName = Text(root, "shortName", problems);
            var accentColor = Text(root, "accentColor", problems);
            var description = Text(root, "description", problems);
            var served = Text(root, "root", problems) ?? "";
            var reads = Texts(root, "reads", problems);
            var connect = Texts(root, "connect", problems);
            var icon = Text(root, "icon", problems);
            var instructions = Text(root, "instructions", problems);
            var asks = Texts(root, "asks", problems);
            var model = Text(root, "model", problems);

            if (string.IsNullOrWhiteSpace(name) || name.Length > NameLength)
            {
                problems.Add($"name is required: 1–{NameLength} characters.");
            }

            if (shortName is not null && (shortName.Trim().Length == 0 || shortName.Length > ShortNameLength))
            {
                problems.Add($"shortName, when given, is 1–{ShortNameLength} characters (what a home screen shows under the icon).");
            }

            if (!SpaceConfig.IsValidHexColor(accentColor))
            {
                problems.Add("accentColor is required and is a hex colour (#rgb, #rrggbb or #rrggbbaa).");
            }

            if (string.IsNullOrWhiteSpace(description) || description.Length > DescriptionLength)
            {
                problems.Add($"description is required: 1–{DescriptionLength} characters.");
            }

            if (!IsServedDirectory(served))
            {
                problems.Add(
                    $"root '{served}' must be a directory of the repository, relative to it (such as dist), "
                    + "never starting with /, never climbing out with .., never under node_modules or .git.");
            }

            problems.AddRange(reads.Where(r => !IsMountPoint(r))
                .Select(r => $"reads: '{r}' is no mount point: write a slash and a mount's name, such as /sugary."));
            problems.AddRange(Twice(reads).Select(r => $"reads: '{r}' is named twice."));
            problems.AddRange(connect.Where(c => !IsBareHttpsOrigin(c))
                .Select(c => $"connect: '{c}' is no bare https origin: write https://host or https://host:port, with no path, no trailing slash and no wildcard."));
            problems.AddRange(Twice(connect).Select(c => $"connect: '{c}' is named twice."));

            if (icon is not null && !AppIcon.IsServedPath(icon))
            {
                problems.Add($"icon '{icon}' must be the path the app serves an SVG drawing at, such as /icon.svg: starting with /, "
                             + "ending in .svg, never under /_app/ (the server's own), never climbing out with ..");
            }

            if (instructions is not null && !IsInstructionsPath(instructions))
            {
                problems.Add($"instructions '{instructions}' must be the path of a markdown file of the repository, from its root, "
                             + "such as instructions.md or asks/estimate.md: ending in .md, never starting with /, "
                             + "never climbing out with .., never under node_modules or .git.");
            }

            problems.AddRange(asks.Where(a => !TunnelCredentialProtocol.IsServerName(a))
                .Select(a => $"asks: '{a}' is no server's name: write the name it is configured under, such as sugary or vault."));
            problems.AddRange(Twice(asks).Select(a => $"asks: '{a}' is named twice."));

            if (model is not null && !IsModelId(model))
            {
                problems.Add($"model '{model}' must be a model id as your patchable models name it, such as z-ai/glm-5.3-flash: "
                             + $"a vendor, a slash and a name, no spaces, at most {ModelLength} characters.");
            }

            return problems.Count > 0
                ? new AppManifestParse.Refused(problems)
                : new AppManifestParse.Valid(new AppManifest
                {
                    Name = name!,
                    ShortName = shortName,
                    AccentColor = accentColor!,
                    Description = description!,
                    Root = served.TrimEnd('/'),
                    Reads = reads,
                    Connect = connect,
                    Icon = icon,
                    Instructions = instructions,
                    Asks = asks,
                    Model = model
                });
        }
    }

    // A mount point as the agent's tree names one: a slash and a mount's name.
    public static bool IsMountPoint(string value) => value.StartsWith('/') && Slugs.IsValid(value[1..]);

    public static bool IsBareHttpsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !value.Contains('*')
        && uri.UserInfo.Length == 0
        && uri.GetLeftPart(UriPartial.Authority).Equals(value, StringComparison.OrdinalIgnoreCase);

    private static bool IsServedDirectory(string path) =>
        path.Length == 0
        || (!path.StartsWith('/') && !path.Contains('\\')
            && path.TrimEnd('/').Split('/').All(segment =>
                segment.Length > 0 && segment is not ("." or ".." or "node_modules" or ".git")));

    private static bool IsInstructionsPath(string path) =>
        path.EndsWith(".md", StringComparison.Ordinal) && !path.StartsWith('/') && !path.Contains('\\')
        && path.Split('/').All(segment =>
            segment.Length > 0 && !segment.Any(char.IsControl) && segment is not ("." or ".." or "node_modules" or ".git"));

    private static bool IsModelId(string model) =>
        model.Length <= ModelLength
        && model.IndexOf('/') is > 0 and var slash && slash < model.Length - 1
        && !model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static IEnumerable<string> Twice(IReadOnlyList<string> values) =>
        values.GroupBy(v => v, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);

    private static string? Text(JsonElement root, string field, List<string> problems)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        problems.Add($"{field} must be a string.");
        return null;
    }

    private static IReadOnlyList<string> Texts(JsonElement root, string field, List<string> problems)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String))
        {
            return [.. value.EnumerateArray().Select(v => v.GetString()!)];
        }

        problems.Add($"{field} must be a list of strings.");
        return [];
    }
}

public abstract record AppManifestParse
{
    public sealed record Valid(AppManifest Manifest) : AppManifestParse;

    public sealed record Refused(IReadOnlyList<string> Problems) : AppManifestParse;
}