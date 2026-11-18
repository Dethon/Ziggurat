using System.Text;

namespace Domain.Tools.FileSystem;

// One action file run by itself, `./name args`, read the way bash would read it — or not at all.
// The parser accepts only lines whose words it can prove: outside quotes, characters bash gives no
// meaning to; single quotes as bash keeps them; double quotes holding nothing bash would expand;
// a backslash escaping the next character. Anything else — a glob, a brace, a tilde, a comment, an
// operator, an open quote, a space bash would not split on — is null, which costs the caller only
// the sandbox it would have run in anyway. BareActionTests holds the parser to bash itself.
public sealed record BareAction(string Name, IReadOnlyList<string> Args)
{
    // The line an action file in the sandbox hands its mount (VfsCall.ActionAsync): the name, then
    // each word quoted so the mounts' tokenizers read it back as the one word it was.
    public string Command => Render(Name, Args);

    public static string Render(string name, IReadOnlyList<string> args) =>
        string.Join(' ', args.Select(Quote).Prepend($"./{name}"));

    public static BareAction? Parse(string command)
    {
        var words = Words(command);
        if (words is not [var first, ..] || !first.StartsWith("./", StringComparison.Ordinal))
        {
            return null;
        }

        var name = first[2..];
        return IsName(name) && NamedPlainly(command, name)
            ? new BareAction(name, words[1..])
            : null;
    }

    private static string Quote(string word) =>
        word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || "-_./=:,@%+".Contains(c))
            ? word
            : $"'{word.Replace("'", "'\\''")}'";

    private static bool IsName(string name) =>
        name is not ("" or "." or "..")
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '+');

    // The name as typed, unquoted and unescaped: `./'x'` is the same program to bash, but a line
    // that spells it so is not the plain action line this route exists for.
    private static bool NamedPlainly(string command, string name)
    {
        var line = command.TrimStart(' ', '\t');
        var typed = $"./{name}";
        return line.StartsWith(typed, StringComparison.Ordinal)
               && (line.Length == typed.Length || line[typed.Length] is ' ' or '\t');
    }

    // The words bash would pass, or null where bash would do anything but split on spaces and tabs
    // and remove quotes.
    private static List<string>? Words(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var i = 0;

        while (i < command.Length)
        {
            var c = command[i];
            if (c is ' ' or '\t')
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }

                i++;
                continue;
            }

            var startsWord = !inWord;
            inWord = true;
            switch (c)
            {
                case '\'':
                    var close = command.IndexOf('\'', i + 1);
                    if (close < 0 || command.AsSpan(i + 1, close - i - 1).ContainsAny('\n', '\r'))
                    {
                        return null;
                    }

                    word.Append(command, i + 1, close - i - 1);
                    i = close + 1;
                    break;

                case '"':
                    if (DoubleQuoted(command, i + 1, word) is not { } end)
                    {
                        return null;
                    }

                    i = end + 1;
                    break;

                case '\\':
                    if (i + 1 >= command.Length || command[i + 1] is '\n' or '\r')
                    {
                        return null;
                    }

                    word.Append(command[i + 1]);
                    i += 2;
                    break;

                // A comment only where a word begins; inside one it is a character like any other.
                case '#' when !startsWord:
                    word.Append(c);
                    i++;
                    break;

                default:
                    if (!IsPlain(c))
                    {
                        return null;
                    }

                    word.Append(c);
                    i++;
                    break;
            }
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    // Inside double quotes bash expands `$` and backquotes, and a backslash escapes only `$`, a
    // backquote, `"`, `\` and a newline, staying literal before anything else. The expansions are
    // refused outright, escaped or not. Answers the index of the closing quote.
    private static int? DoubleQuoted(string command, int start, StringBuilder word)
    {
        var i = start;
        while (i < command.Length)
        {
            var c = command[i];
            switch (c)
            {
                case '"':
                    return i;
                case '$' or '`' or '\n' or '\r':
                    return null;
                case '\\' when i + 1 < command.Length && command[i + 1] is '"' or '\\':
                    word.Append(command[i + 1]);
                    i += 2;
                    continue;
                case '\\' when i + 1 < command.Length && command[i + 1] is '$' or '`' or '\n' or '\r':
                    return null;
                default:
                    word.Append(c);
                    i++;
                    continue;
            }
        }

        return null;
    }

    // Characters bash reads as themselves anywhere in an unquoted word. Letters beyond ASCII are
    // plain (bash splits on its IFS, space, tab and newline, alone), but a space of any other kind
    // is not admitted: bash would keep it inside the word, and a reader of the line would not.
    private static bool IsPlain(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c is '_' or '-' or '.' or '/' or ':' or '=' or ',' or '+' or '@' or '%'
        || (c > 127 && char.IsLetterOrDigit(c));
}