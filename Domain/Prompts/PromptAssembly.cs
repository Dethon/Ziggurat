namespace Domain.Prompts;

// The assembled system prompt, and the sections it was assembled from. Keeping the sections
// rather than only the text is the point: a snapshot can print what each one cost, a test can ask
// where the voice rules landed relative to the formatting they override, and a warning can name
// the section that grew instead of reporting that the prompt did.
public sealed record PromptAssembly(IReadOnlyList<PromptSection> Sections, IReadOnlyList<string> Warnings)
{
    public string Text => string.Join("\n\n", Sections.Select(s => s.Text));

    public int TokenCount => Sections.Sum(s => s.TokenCount);

    public static PromptAssembly Build(string agentId, IEnumerable<PromptSection> sections)
    {
        var ordered = sections
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .Where(s => s.Declaration.Audience.Includes(agentId))
            // Stable, so the order sections were offered in survives inside a band — which is how
            // several MCP servers' prompts keep the order their endpoints were configured in.
            .OrderBy(s => (int)s.Priority)
            .ToList();

        return new PromptAssembly(
            ordered,
            [.. Undeclared(ordered).Concat(OverBudget(ordered)).Concat(Contradictions(ordered))]);
    }

    private static IEnumerable<string> Undeclared(IReadOnlyList<PromptSection> sections) =>
        sections
            .Where(s => !s.Declaration.Declared)
            .Select(s =>
                $"prompt section '{s.Name}' is not declared in the manifest; it was assembled at " +
                $"{s.TokenCount} tokens with no budget and no stated place");

    private static IEnumerable<string> OverBudget(IReadOnlyList<PromptSection> sections) =>
        sections
            .Where(s => s.IsOverBudget)
            .Select(s =>
                $"prompt section '{s.Name}' is {s.TokenCount} tokens, over its budget of " +
                $"{s.Declaration.TokenBudget}");

    // Every rule claimed by more than one section, and for each pair the question of who wins.
    // An unanswered pair is the defect this exists to surface: two sections telling the model
    // different things about the same rule, with nothing but their distance from the conversation
    // deciding which one it follows.
    private static IEnumerable<string> Contradictions(IReadOnlyList<PromptSection> sections)
    {
        var pairs = sections
            .SelectMany((section, index) => section.Declaration.Conflict.Claims.Select(
                rule => (Rule: rule, Section: section, Index: index)))
            .GroupBy(claim => claim.Rule, StringComparer.OrdinalIgnoreCase)
            .SelectMany(rule => rule.SelectMany(
                (first, i) => rule.Skip(i + 1).Select(second => (rule.Key, First: first, Second: second))));

        return pairs.SelectMany(pair => Resolve(pair.Key, pair.First, pair.Second));
    }

    private static IEnumerable<string> Resolve(
        string rule,
        (string Rule, PromptSection Section, int Index) first,
        (string Rule, PromptSection Section, int Index) second)
    {
        var firstBeatsSecond = Overrides(first.Section, second.Section);
        var secondBeatsFirst = Overrides(second.Section, first.Section);

        if (!firstBeatsSecond && !secondBeatsFirst)
        {
            yield return
                $"prompt sections '{first.Section.Name}' and '{second.Section.Name}' both govern " +
                $"'{rule}' and neither declares which one wins";
            yield break;
        }

        // An override is a claim about assembly order, so it can be wrong: the winner has to be
        // read last, and a section that overrides something below it is overridden by it in fact.
        if (firstBeatsSecond && first.Index < second.Index)
        {
            yield return Misordered(first.Section, second.Section, rule);
        }

        if (secondBeatsFirst && second.Index < first.Index)
        {
            yield return Misordered(second.Section, first.Section, rule);
        }
    }

    private static string Misordered(PromptSection winner, PromptSection loser, string rule) =>
        $"prompt section '{winner.Name}' overrides '{loser.Name}' on '{rule}' but is assembled " +
        "before it, so the model reads the overridden rule last";

    private static bool Overrides(PromptSection section, PromptSection other) =>
        section.Declaration.Conflict.Overrides.Contains(other.Name, StringComparer.OrdinalIgnoreCase);
}