using System.Text.Json;

namespace Domain.Tools.Web;

// What a call that acts as the person is approved for: the tool on one site, so approving it for
// a shop never covers the person's mail. A call acts as the person when its arguments carry
// `asMe: true` — a flag the model chooses per call, which a page's own text can ask it to set — so
// the agent's approval gate asks about it by this subject rather than by the tool alone. A call
// whose url will not parse is still the person's, scoped to the tool with no site, and so can
// never be covered by a remembered site.
public sealed record AsPersonSubject(string Tool, string? Site)
{
    public const string FlagArgument = "asMe";
    public const string UrlArgument = "url";

    public static AsPersonSubject? Of(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue(FlagArgument, out var flag) || !IsTrue(flag))
        {
            return null;
        }

        var site = CallArguments.Text(arguments, UrlArgument) is { } url
                   && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                   && !string.IsNullOrEmpty(uri.Host)
            ? RegistrableDomain.Of(uri.Host)
            : null;
        return new AsPersonSubject(toolName, site);
    }

    private static bool IsTrue(object? value) => value switch
    {
        bool flag => flag,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.String } text =>
            string.Equals(text.GetString(), "true", StringComparison.OrdinalIgnoreCase),
        string text => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}