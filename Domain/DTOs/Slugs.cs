using System.Text.RegularExpressions;

namespace Domain.DTOs;

// One shape for every name that rides unescaped in a Redis key, a URL path or a chat command — a
// space's slug, an agent's id — so a name one side takes is never one the other refuses.
public static partial class Slugs
{
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}