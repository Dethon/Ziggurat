namespace Domain.Prompts;

// Every section that can reach a system prompt, declared in one table read from the section
// documents. What a section is for, where it sits, what it may cost, who it is for and what it beats
// are answered in its document's header rather than distributed between an assembly method, a
// settings file and the prose of the prompt itself.
//
// Text is bound to a declaration rather than stored in it, because half of these sections are
// written by somebody else: an MCP server serves its own prompt and may change it without this
// repo being rebuilt. What is declared for those is the budget the deployment expects them to fit
// and the place they are read in — which is exactly the part a test can hold still.
public static class PromptManifest
{
    // What an MCP server's prompt is allowed to cost before somebody has thought about it. Reached
    // only by a prompt with no declaration, which is a state the staleness tests fail on for every
    // server this repo owns.
    public const int UndeclaredBudget = 1_500;

    // What a skill nobody declared may cost, description and body, before somebody has thought
    // about it — the same rule as an undeclared prompt, for the same kind of server.
    public const int UndeclaredSkillDescriptionBudget = 100;
    public const int UndeclaredSkillBodyBudget = 1_500;

    // What the largest agent's standing sections are budgeted to, rounded up to the hundred. It is
    // a ratchet, not a limit: every section that moves behind a skill lowers it by editing this one
    // number, and the budget tests refuse a figure left where it was, so what left the base prompt
    // cannot grow back into the room it vacated. A section whose declared budget grows raises it
    // the same way: the figure follows the declarations and is never set ahead of them.
    public const int StandingTokens = 9_200;

    // What an agent's whole prompt may cost above that: the slack for a section that ran over its
    // budget, or one that arrived from a server nobody declared, before a turn is paying for a
    // prompt nobody agreed to.
    public const int Headroom = 600;

    // What one agent's whole system prompt may cost. It is re-sent on every request of every
    // conversation, and what it does not take is what the conversation itself gets — so this is a
    // ceiling on the static prefix rather than on the context window, and the sum of the budgets
    // below has to stay under it or the table is decoration.
    public const int MaxAgentPromptTokens = StandingTokens + Headroom;

    // The ceiling check, with the standing figure as a parameter so a test can show what lowering
    // it does to an agent whose sections add up to more.
    public static bool FitsCeiling(int tokens, int standingTokens = StandingTokens) =>
        tokens <= standingTokens + Headroom;

    // Read from the section documents (Domain/Prompts/Sections), whose headers are these
    // declarations: a section's purpose, band, budget, audience, conflicts and claims sit at the top
    // of the file holding its words. In band order, then by name, so the table reads the way the
    // prompt does.
    public static IReadOnlyList<PromptDeclaration> Declarations { get; } =
    [
        .. PromptDocuments.Sections
            .Select(document => document.AsSection())
            .OrderBy(declaration => (int)declaration.Priority)
            .ThenBy(declaration => declaration.Name, StringComparer.Ordinal)
    ];

    // Every skill a server in this deployment ships, declared beside the sections and read from its
    // document (Domain/Prompts/Skills) the same way. A skill is read on demand, so its body's budget
    // is what one load costs a conversation and its description's budget is what every turn pays to
    // advertise it. By name: the order a model sees them in is its servers', not this table's.
    public static IReadOnlyList<SkillDeclaration> Skills { get; } =
        [.. PromptDocuments.Skills.Select(document => document.AsSkill())];

    public const string CoreDirective = CoreDirectivePrompt.Name;
    public const string SkillsSection = "skills";
    public const string Identity = "identity";
    public const string UserContext = "user_context";
    public const string Subagents = "subagents";
    public const string Memory = "memory";
    public const string FilesystemMounts = "filesystem_mounts";
    public const string Date = "date";
    public const string CustomInstructions = "custom_instructions";
    public const string Language = "language";

    private static readonly Dictionary<string, PromptDeclaration> _byName =
        Declarations.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    // Sections whose words live in this repo and are chosen by name in an agent's configuration —
    // a document whose header says `selectable: true`. Everything else is bound by whoever produces
    // its text.
    private static readonly Dictionary<string, string> _selectable = PromptDocuments.Sections
        .Where(document => document.IsSelectable)
        .ToDictionary(document => document.Name, document => document.Text, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> SelectableSections => _selectable.Keys;

    // The selectable sections written for a channel (audience-channels), which an agent serving a
    // conversation built for that channel reads whatever its definition selects: voice's rules for
    // whichever agent a household has answer aloud.
    public static IReadOnlyList<string> ForChannel(string? channelId) =>
        channelId is null
            ? []
            :
            [
                .. SelectableSections.Where(name => Find(name) is { } declaration
                    && declaration.Audience.Channels.Contains(channelId, StringComparer.OrdinalIgnoreCase))
            ];

    // Aggregated across sections the way the declarations themselves are, so a scenario can cite
    // one id and a coverage test can enumerate every claim the deployment makes.
    public static IReadOnlyList<PromptClaim> Claims { get; } =
        [.. Declarations.SelectMany(d => d.Claims), .. Skills.SelectMany(s => s.Claims)];

    private static readonly Dictionary<string, SkillDeclaration> _skillsByName =
        Skills.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static SkillDeclaration? FindSkill(string name) => _skillsByName.GetValueOrDefault(name);

    // A skill a server served, under the declaration that governs it. An undeclared one is still
    // offered, for the reason an undeclared prompt is still assembled: the deployment's business,
    // not this turn's. It is marked, and the assembly says so.
    public static PromptSkill BindSkill(string name, string description, string body) =>
        (FindSkill(name) ?? UndeclaredSkill(name)).Bind(description, body);

    public static PromptDeclaration? Find(string name) => _byName.GetValueOrDefault(name);

    // A section by name, for configuration that selects one. Null for a name nothing declares, so
    // the caller can refuse a misconfigured agent by name rather than start one prompt short.
    public static PromptSection? Selected(string name) =>
        _selectable.TryGetValue(name, out var text) && Find(name) is { } declaration
            ? declaration.Bind(text)
            : null;

    // Text from elsewhere — an MCP server's prompt, a feature's, a per-session one — under the
    // declaration that governs it. An undeclared name still assembles: refusing it would take a
    // turn down over an MCP server that added a prompt, which is the deployment's business and not
    // this turn's. It is marked, and the assembly says so.
    public static PromptSection Bind(string name, string text) =>
        (Find(name) ?? Undeclared(name)).Bind(text);

    // Every section name a deployment's agents ask for, checked in one pass. The projection refuses
    // an unknown name too, but only when that agent is first built — which is a first message on a
    // channel nobody has used since the config changed.
    public static void Validate(IEnumerable<(string AgentId, IEnumerable<string> Sections)> agents)
    {
        var unknown = agents
            .SelectMany(a => a.Sections.Select(name => (a.AgentId, Name: name)))
            .Where(named => Selected(named.Name) is null)
            .Select(named => $"{named.AgentId} -> '{named.Name}'")
            .ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"promptSections names sections the manifest does not declare as selectable: " +
                $"{string.Join(", ", unknown)}. Available: {string.Join(", ", SelectableSections)}.");
        }
    }

    private static SkillDeclaration UndeclaredSkill(string name) => new()
    {
        Name = name,
        Description = string.Empty,
        DescriptionBudget = UndeclaredSkillDescriptionBudget,
        BodyBudget = UndeclaredSkillBodyBudget,
        ServedBy = "undeclared",
        Declared = false
    };

    private static PromptDeclaration Undeclared(string name) => new()
    {
        Name = name,
        Purpose = "Served by an MCP server this deployment has not declared.",
        Priority = PromptPriority.Client,
        TokenBudget = UndeclaredBudget,
        Declared = false
    };
}