using System.Text.Json;
using System.Text.RegularExpressions;

namespace Domain.Extensions;

public static class StringExtensions
{
    extension(string str)
    {
        public string Left(int count)
        {
            return str.Length <= count ? str : str[..count];
        }

        // The words of an answer that is one JSON object (a structured format's), in the order it
        // holds them, joined as one line; null for anything else. What a preview of it shows, rather
        // than its braces.
        public string? JsonObjectWords()
        {
            if (!str.TrimStart().StartsWith('{'))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(str);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var words = Strings(document.RootElement).Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
                return words.Count == 0 ? null : string.Join(" · ", words);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // What a reply says, read as one line of text: markdown's marks dropped and its lines joined.
        // A fenced block's lines are kept as written, its fences dropped; outside one, a heading's,
        // quote's and bullet's marker goes, and emphasis, inline code and a link's target. A mark
        // only counts where markdown would read it as one, so "2 * 3" and "snake_case" stay as they are.
        public string MarkdownToPlainText()
        {
            var lines = new List<string>();
            var fenced = false;
            foreach (var line in str.Split('\n'))
            {
                if (_fence.IsMatch(line))
                {
                    fenced = !fenced;
                    continue;
                }

                lines.Add(fenced ? line : Inline(_lineMarker.Replace(line, "")));
            }

            return _whitespace.Replace(string.Join(' ', lines), " ").Trim();
        }

        public string HtmlSanitize()
        {
            return str
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }

    private static readonly Regex _fence = new(@"^\s{0,3}(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex _lineMarker = new(@"^\s{0,3}(#{1,6}\s+|>\s?|[-*+]\s+)", RegexOptions.Compiled);
    private static readonly Regex _whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly (Regex Mark, string Keep)[] _inline =
    [
        (new Regex(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled), "$1"),
        (new Regex(@"`([^`]*)`", RegexOptions.Compiled), "$1"),
        (new Regex(@"\*\*(?!\s)(.+?)(?<!\s)\*\*", RegexOptions.Compiled), "$1"),
        (new Regex(@"(?<!\w)__(?!\s)(.+?)(?<!\s)__(?!\w)", RegexOptions.Compiled), "$1"),
        (new Regex(@"~~(?!\s)(.+?)(?<!\s)~~", RegexOptions.Compiled), "$1"),
        (new Regex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", RegexOptions.Compiled), "$1"),
        (new Regex(@"(?<!\w)_(?!\s)(.+?)(?<!\s)_(?!\w)", RegexOptions.Compiled), "$1")
    ];

    private static string Inline(string line) =>
        _inline.Aggregate(line, (text, rule) => rule.Mark.Replace(text, rule.Keep));

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!.Trim()],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        _ => []
    };
}