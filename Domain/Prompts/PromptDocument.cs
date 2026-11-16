using System.Globalization;
using System.Text.RegularExpressions;

namespace Domain.Prompts;

// A prompt's words as a text document: a small header saying what the words are for, and the
// words, with named holes for what only the host knows. The header is the declaration — a
// section's purpose, place, budget, audience, conflicts and claims, or a skill's advertisement,
// budgets, server and claims — so a claim sits beside the prose that teaches it (ADR 0031) in the
// one file an edit of that prose touches.
//
// The format is this repo's, not YAML: a header line is `key: value`, split at the first colon,
// because a purpose or a claim is a sentence and sentences have colons in them. `#` starts a
// comment line, which is where the reason a budget is what it is lives. A body may carry named
// parts after it, each opened by an `<!-- part: name -->` line, for the words a host adds or swaps
// in — a roster's heading, a language's rule — so every sentence the model reads is in a document
// and the host only chooses and fills.
//
// A placeholder is `{{name}}`, lowercase with no spaces, so Jinja's and Templater's spaced braces in
// a skill body stay prose. The header lists every placeholder its bodies use, and the two are held
// to each other when the document is read: an edit that drops a hole from the words fails there.
// The host states what it fills when it takes a body (`Template`, `Part`), which fails on a hole
// dropped from the header and the words alike.
public sealed partial record PromptDocument
{
    private const string Fence = "---";
    private const string ClaimsKey = "claims";
    private const string PlaceholdersKey = "placeholders";

    // What a section's header may say. Anything else is a typo the manifest would otherwise read as
    // nothing.
    private static readonly HashSet<string> _sectionKeys =
    [
        "name", "purpose", "priority", "budget", "served-by", "audience-agents", "audience-channels",
        "governs", "beats", "selectable", PlaceholdersKey, ClaimsKey
    ];

    // What a skill's header may say: the advertisement and what it and the body may cost, the server
    // that ships it, and the files a preload reads beside it.
    private static readonly HashSet<string> _skillKeys =
    [
        "name", "description", "description-budget", "body-budget", "served-by", "preload-reads",
        PlaceholdersKey, ClaimsKey
    ];

    // Where the document came from, for every error it raises: a file name in the repo, or a
    // resource's.
    public required string Source { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyDictionary<string, string> Header { get; init; }

    public required IReadOnlyList<PromptClaim> Claims { get; init; }

    public required IReadOnlySet<string> Placeholders { get; init; }

    public required string Body { get; init; }

    public required IReadOnlyDictionary<string, string> Parts { get; init; }

    // The main body as it is, for a document with nothing to fill.
    public string Text => Template().Fill();

    public string? Value(string key) => Header.GetValueOrDefault(key);

    public PromptClaim Claim(string id) =>
        Claims.FirstOrDefault(claim => claim.Id == id)
        ?? throw new InvalidOperationException($"{Source} declares no claim '{id}'");

    public PromptTemplate Template(params string[] fills) => Bind(Source, Body, fills);

    public PromptTemplate Part(string name, params string[] fills) =>
        Parts.TryGetValue(name, out var body)
            ? Bind($"{Source} (part '{name}')", body, fills)
            : throw new InvalidOperationException($"{Source} has no part '{name}'");

    public static PromptDocument Parse(string source, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var close = lines.Length > 0 && lines[0] == Fence ? Array.IndexOf(lines, Fence, 1) : -1;
        if (close < 0)
        {
            throw new FormatException(
                $"{source}: a prompt document opens with its header, between two '{Fence}' lines");
        }

        var (header, claims) = ReadHeader(source, lines[1..close]);
        var (body, parts) = ReadBodies(source, lines[(close + 1)..], close + 2);
        var placeholders = List(header.GetValueOrDefault(PlaceholdersKey)).ToHashSet(StringComparer.Ordinal);

        Hold(source, placeholders, [body, .. parts.Values]);

        return new PromptDocument
        {
            Source = source,
            Name = header.GetValueOrDefault("name")
                   ?? throw new FormatException($"{source}: the header names no section ('name:')"),
            Header = header,
            Claims = claims,
            Placeholders = placeholders,
            Body = body,
            Parts = parts
        };
    }

    // The header read as a section's declaration.
    public PromptDeclaration AsSection()
    {
        Known("section", _sectionKeys);

        var rules = List(Value("governs")).ToList();
        var unruled = rules.Where(rule => !PromptRules.All.Contains(rule)).ToList();
        if (unruled.Count > 0)
        {
            throw new FormatException(
                $"{Source}: 'governs' names '{string.Join("', '", unruled)}', which is not a rule in PromptRules");
        }

        if (Value("selectable") is { } selectable && selectable is not ("true" or "false"))
        {
            throw new FormatException($"{Source}: 'selectable' is true or false, not '{selectable}'");
        }

        return new PromptDeclaration
        {
            Name = Name,
            Purpose = Required("purpose"),
            // By band name only: a number would place a section between bands nobody declared.
            Priority = Enum.GetNames<PromptPriority>().Contains(Required("priority"), StringComparer.Ordinal)
                ? Enum.Parse<PromptPriority>(Required("priority"))
                : throw new FormatException($"{Source}: '{Required("priority")}' is not a PromptPriority band"),
            TokenBudget = Tokens("budget"),
            ServedBy = Value("served-by"),
            Audience = new PromptAudience
            {
                Agents = [.. List(Value("audience-agents"))],
                Channels = [.. List(Value("audience-channels"))]
            },
            Conflict = ConflictPolicy.Governs([.. rules]).Beating([.. List(Value("beats"))]),
            Claims = Claims
        };
    }

    // The header read as a skill's declaration (docs/adr/0039). A skill always names its server: it
    // teaches that server's tools and is served beside them.
    public SkillDeclaration AsSkill()
    {
        Known("skill", _skillKeys);

        return new SkillDeclaration
        {
            Name = Name,
            Description = Required("description"),
            DescriptionBudget = Tokens("description-budget"),
            BodyBudget = Tokens("body-budget"),
            ServedBy = Required("served-by"),
            Claims = Claims,
            PreloadReads = [.. List(Value("preload-reads"))]
        };
    }

    public bool IsSelectable => Value("selectable") == "true";

    private void Known(string kind, HashSet<string> keys)
    {
        var unknown = Header.Keys.Where(key => !keys.Contains(key)).ToList();
        if (unknown.Count > 0)
        {
            throw new FormatException($"{Source}: a {kind}'s header has no '{string.Join("', '", unknown)}'");
        }
    }

    private string Required(string key) =>
        Value(key) ?? throw new FormatException($"{Source}: the header has no '{key}'");

    private int Tokens(string key) =>
        int.TryParse(Required(key), NumberStyles.None, CultureInfo.InvariantCulture, out var tokens)
            ? tokens
            : throw new FormatException($"{Source}: the {key} '{Required(key)}' is not a whole number of tokens");

    private static (Dictionary<string, string> Header, List<PromptClaim> Claims) ReadHeader(
        string source, string[] lines)
    {
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        var claims = new List<PromptClaim>();
        var inClaims = false;

        foreach (var (line, number) in lines.Select((line, index) => (line, index + 2)))
        {
            if (line.Trim().Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                var (id, statement) = Split(source, number, line[2..]);
                if (!inClaims)
                {
                    throw new FormatException($"{source}:{number}: a list item outside 'claims:'");
                }

                if (claims.Any(claim => claim.Id == id))
                {
                    throw new FormatException($"{source}:{number}: the claim '{id}' is declared twice");
                }

                claims.Add(new PromptClaim(id, statement));
                continue;
            }

            var (key, value) = Split(source, number, line, allowEmpty: true);
            if (!KeyPattern().IsMatch(key) || !header.TryAdd(key, value))
            {
                throw new FormatException($"{source}:{number}: '{key}' is not a key, or is given twice");
            }

            inClaims = key == ClaimsKey;
            if (inClaims != (value.Length == 0))
            {
                throw new FormatException($"{source}:{number}: only 'claims:' opens a list, and it takes no value");
            }
        }

        return (header, claims);
    }

    private static (string Body, Dictionary<string, string> Parts) ReadBodies(
        string source, string[] lines, int firstLine)
    {
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        var current = (Name: (string?)null, Lines: new List<string>());
        var body = (string?)null;

        foreach (var (line, number) in lines.Select((line, index) => (line, index + firstLine)))
        {
            var marker = PartPattern().Match(line);
            if (!marker.Success)
            {
                current.Lines.Add(line);
                continue;
            }

            Close(current);
            current = (marker.Groups[1].Value, []);
            if (parts.ContainsKey(current.Name!))
            {
                throw new FormatException($"{source}:{number}: the part '{current.Name}' is opened twice");
            }
        }

        Close(current);
        return (body!, parts);

        void Close((string? Name, List<string> Lines) segment)
        {
            var text = string.Join("\n", segment.Lines).Trim('\n');
            if (segment.Name is null)
            {
                body = text;
            }
            else
            {
                parts[segment.Name] = text;
            }
        }
    }

    // Every hole the bodies use is declared, and every declared one is used somewhere.
    private static void Hold(string source, IReadOnlySet<string> declared, IEnumerable<string> bodies)
    {
        var used = bodies.SelectMany(Holes).ToHashSet(StringComparer.Ordinal);
        var dropped = declared.Where(name => !used.Contains(name)).ToList();
        var undeclared = used.Where(name => !declared.Contains(name)).ToList();

        if (dropped.Count > 0)
        {
            throw new FormatException(
                $"{source}: the header declares {Spelled(dropped)}, which no body uses — the host fills " +
                "it, so the words have to keep it");
        }

        if (undeclared.Count > 0)
        {
            throw new FormatException(
                $"{source}: the words use {Spelled(undeclared)}, which the header does not declare under " +
                $"'{PlaceholdersKey}:'");
        }
    }

    private static PromptTemplate Bind(string source, string body, IReadOnlyList<string> fills)
    {
        var holes = Holes(body).ToHashSet(StringComparer.Ordinal);
        if (!holes.SetEquals(fills))
        {
            throw new InvalidOperationException(
                $"{source}: the host fills {Spelled(fills)} and the words hold {Spelled(holes)}; a hole the " +
                "host fills cannot be dropped from the document, nor one it does not fill added");
        }

        return new PromptTemplate(source, body, holes);
    }

    internal static IEnumerable<string> Holes(string body) =>
        PlaceholderPattern().Matches(body).Select(match => match.Groups[1].Value);

    internal static string Spelled(IEnumerable<string> names)
    {
        var list = names.Order(StringComparer.Ordinal).Select(name => $"{{{{{name}}}}}").ToList();
        return list.Count == 0 ? "nothing" : string.Join(", ", list);
    }

    private static (string Key, string Value) Split(string source, int number, string line, bool allowEmpty = false)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0 || (!allowEmpty && colon == line.Length - 1))
        {
            throw new FormatException($"{source}:{number}: '{line}' is not 'key: value'");
        }

        return (line[..colon].Trim(), line[(colon + 1)..].Trim());
    }

    private static IEnumerable<string> List(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"\{\{([a-z][a-z0-9_]*)\}\}")]
    internal static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^<!-- part: ([a-z0-9][a-z0-9-]*) -->$")]
    private static partial Regex PartPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*$")]
    private static partial Regex KeyPattern();
}

// One body of a document, taken by the host that fills it. Holding the set of holes here means a
// fill names exactly what the words leave open — a value missing is an error, not a `{{name}}`
// reaching the model.
public sealed record PromptTemplate(string Source, string Body, IReadOnlySet<string> Placeholders)
{
    public string Fill(params (string Name, string Value)[] values)
    {
        var given = values.Select(value => value.Name).ToList();
        if (!Placeholders.SetEquals(given) || given.Count != given.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException(
                $"{Source}: filled with {PromptDocument.Spelled(given)}, but its words hold " +
                PromptDocument.Spelled(Placeholders));
        }

        var byName = values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

        // One pass: a value that happens to spell a hole is the value, and is not filled again.
        return Placeholders.Count == 0
            ? Body
            : PromptDocument.PlaceholderPattern().Replace(Body, match => byName[match.Groups[1].Value]);
    }
}