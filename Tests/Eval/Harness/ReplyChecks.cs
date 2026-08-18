using System.Globalization;
using System.Text.RegularExpressions;

namespace Tests.Eval.Harness;

// What the agent said, checked deterministically. A judge model would be the obvious way to ask
// "was that a good answer", and it is the wrong tool for these: every rule here is about form
// rather than quality, and a form rule that a second model has to be paid to agree with is a rule
// the suite cannot rely on.
public static class ReplyChecks
{
    public static IReadOnlyList<string> Failures(ReplyExpectation expectation, string reply) =>
    [
        .. TooLong(expectation, reply),
        .. Unspeakable(expectation, reply),
        .. Missing(expectation, reply),
        .. Narrated(expectation, reply)
    ];

    // A sentence ends at '.', '!', '?' or '…'. Spanish opens a question with '¿' and closes it the
    // same way English does, so nothing extra is needed to count one — but the reply this suite
    // most wants to bound is exactly that question, so it is worth being sure.
    public static int Sentences(string reply) =>
        Regex.Split(reply.Trim(), @"(?<=[.!?…])\s+")
            .Count(part => part.Trim(' ', '.', '!', '?', '…', '¿', '¡').Length > 0);

    public static int Words(string reply) =>
        reply.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static IEnumerable<string> TooLong(ReplyExpectation expectation, string reply)
    {
        if (expectation.MaxSentences is { } sentences && Sentences(reply) > sentences)
        {
            yield return $"the reply is {Sentences(reply)} sentences against a limit of " +
                         $"{sentences}: \"{reply}\"";
        }

        if (expectation.MaxWords is { } words && Words(reply) > words)
        {
            yield return $"the reply is {Words(reply)} words against a limit of {words}: \"{reply}\"";
        }
    }

    // The five things a text-to-speech engine cannot say. Each is named separately because the fix
    // is different for each: a markdown asterisk is a formatting habit, an entity id is the agent
    // reading its own plumbing aloud.
    private static readonly (string Named, Regex Pattern)[] _unspeakable =
    [
        ("markdown", new Regex(@"\*\*|__|`|^\s*[-*+]\s|^\s*#{1,6}\s|\[[^\]]+\]\([^)]+\)",
            RegexOptions.Multiline)),
        ("a url", new Regex(@"https?://|\bwww\.", RegexOptions.IgnoreCase)),
        ("a file path", new Regex(@"(^|\s)(/[\w.-]+){2,}|[A-Za-z]:\\")),
        ("an entity id", new Regex(
            @"\b(light|switch|climate|sensor|binary_sensor|media_player|calendar|cover|script|scene|input_boolean|automation)\.[a-z0-9_]+\b",
            RegexOptions.IgnoreCase))
    ];

    private static IEnumerable<string> Unspeakable(ReplyExpectation expectation, string reply)
    {
        if (!expectation.Spoken)
        {
            yield break;
        }

        foreach (var (named, pattern) in _unspeakable)
        {
            if (pattern.Match(reply) is { Success: true } match)
            {
                yield return $"a spoken reply carries {named}: \"{match.Value.Trim()}\" in \"{reply}\"";
            }
        }

        if (Emoji(reply) is { } emoji)
        {
            yield return $"a spoken reply carries emoji: \"{emoji}\" in \"{reply}\"";
        }
    }

    // By Unicode category rather than by a list of ranges: what makes a character unspeakable is
    // that it is a symbol or a pictograph, and a list would be out of date by the next emoji
    // release.
    private static string? Emoji(string reply) =>
        reply.EnumerateRunes()
            .Where(rune => rune.Value > 0x2000
                           && CharUnicodeInfo.GetUnicodeCategory(rune.ToString(), 0)
                               is UnicodeCategory.OtherSymbol)
            .Select(rune => rune.ToString())
            .FirstOrDefault();

    // Retention, not phrasing: the scenario declares the value and every spelling of it that counts,
    // because whether "eight" comes back as a numeral, a word, or a word in another language is the
    // model's business and the value surviving at all is the contract's.
    private static IEnumerable<string> Missing(ReplyExpectation expectation, string reply) =>
        expectation.Mentions
            .Where(value => !value.Spellings.Any(
                spelling => reply.Contains(spelling, StringComparison.OrdinalIgnoreCase)))
            .Select(value =>
                $"the reply does not carry {value.Name} (any of {string.Join(", ", value.Spellings)}): " +
                $"\"{reply}\"");

    // The other half of what a reply contract asks for: silence about mechanism. Matched as a
    // fragment rather than a word so one entry covers a verb's conjugations.
    private static IEnumerable<string> Narrated(ReplyExpectation expectation, string reply) =>
        expectation.NeverSays
            .Where(fragment => reply.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .Select(fragment => $"the reply says '{fragment}', which it must not: \"{reply}\"");
}

// What a scenario declares about the answer. Everything is optional: most scenarios are about
// which tool ran, and a reply limit invented to fill the record in would fail on wording.
public sealed record ReplyExpectation
{
    public int? MaxSentences { get; init; }

    public int? MaxWords { get; init; }

    // Read aloud, so it must carry nothing a text-to-speech engine cannot say.
    public bool Spoken { get; init; }

    public IReadOnlyList<SpokenValue> Mentions { get; init; } = [];

    public IReadOnlyList<string> NeverSays { get; init; } = [];
}

// A value that must survive into the reply, and every spelling that counts as carrying it.
public sealed record SpokenValue(string Name, params string[] Spellings);