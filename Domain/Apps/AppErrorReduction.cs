using System.Text.RegularExpressions;

namespace Domain.Apps;

// What the server keeps of a runtime error another member's browser reported. A shared app runs on every
// member's phone and its author reads the errors file, so every word of a report — the message, the stack,
// the route, a read's path and envelope, even the User-Agent — is the page's to write and could carry what
// it read about its viewer to its author, past the reach an owner approved. So only what the server can
// check against what it serves is kept, each part from a fixed set:
// - the kind (the shape's three);
// - the error's type, when it is a standard one, else Error;
// - the stack's places on the app's own origin, in files it serves (Places hands the candidates; the
//   server keeps those it serves), at most MaxFrames, path, line and column alone;
// - the route, when it names something the origin serves;
// - a read's operation, its mount point when the origin's manifest reads it, and its envelope's code.
// What remains is a numeric channel (which frames, their lines and columns, which kind, type and served
// route), bounded by the frames and the per-session minute.
public static partial class AppErrorReduction
{
    public const string Note =
        "Another member's report: only what the server could check is kept (the error's type, its places in the app's "
        + "own files, a served route, a read's operation, mount and code), never the text their page wrote. Ask them "
        + "what they did, or run ./check.";

    public const int MaxFrames = 10;

    public static readonly IReadOnlyList<string> StandardNames =
        ["Error", "TypeError", "RangeError", "SyntaxError", "ReferenceError", "EvalError", "URIError", "AggregateError"];

    // The type the library names an Error by (`name: message`), when it is one of the language's own.
    public static string NameOf(string message)
    {
        var colon = message.IndexOf(':');
        var name = (colon >= 0 ? message[..colon] : message).Trim();
        return StandardNames.Contains(name, StringComparer.Ordinal) ? name : "Error";
    }

    // Every place a stack line ends on, on this origin (its authority, the Host the report came to): the
    // path alone, never its query or fragment, innermost first. Whether the origin serves that path is the
    // server's to say.
    public static IReadOnlyList<AppErrorPlace> Places(string? stack, string authority) =>
    [
        .. (stack ?? "").Split('\n')
            .Select(line => Location().Match(line))
            .Where(match => match.Success)
            .Select(match => (Uri: Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var uri) ? uri : null, Match: match))
            .Where(place => place.Uri is not null
                            && string.Equals(place.Uri.Authority, authority, StringComparison.OrdinalIgnoreCase))
            .Select(place => new AppErrorPlace(Uri.UnescapeDataString(place.Uri!.AbsolutePath),
                int.Parse(place.Match.Groups["line"].Value), int.Parse(place.Match.Groups["column"].Value)))
    ];

    // A read's mount point, when the origin's manifest reads it: the path below is the page's own words.
    public static string? MountOf(string? path, IReadOnlyList<string> reads)
    {
        var segment = path is ['/', ..] ? path.Split('/', 3)[1] : null;
        return segment is { Length: > 0 } && reads.Contains($"/{segment}", StringComparer.Ordinal) ? $"/{segment}" : null;
    }

    // The last location on a stack line: V8's `at name (url:line:column)` or `at url:line:column`, and
    // Firefox's and Safari's `name@url:line:column`.
    [GeneratedRegex(@"(?<url>https?://[^\s()@]+):(?<line>\d{1,7}):(?<column>\d{1,7})\)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Location();
}

public sealed record AppErrorPlace(string Path, int Line, int Column)
{
    public override string ToString() => $"{Path}:{Line}:{Column}";
}