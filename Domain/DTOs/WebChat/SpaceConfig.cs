using System.Text.RegularExpressions;

namespace Domain.DTOs.WebChat;

public partial record SpaceConfig(string Slug, string Name, string AccentColor)
{
    public const string DefaultAccentColor = "#e9601f";
    public const string DefaultSlug = "default";

    private static readonly Regex _slugPattern = SpaceSlugRegex();
    private static readonly Regex _hexColorPattern = HexColorRegex();

    public static bool IsValidSlug(string? slug) => slug is not null && _slugPattern.IsMatch(slug);
    public static bool IsValidHexColor(string? color) => color is not null && _hexColorPattern.IsMatch(color);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled)]
    private static partial Regex SpaceSlugRegex();

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled)]
    private static partial Regex HexColorRegex();
}