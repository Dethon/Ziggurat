using Domain.Tools.FileSystem;

namespace Domain.Prompts;

// Every section that can reach a system prompt, declared in one table. What a section is for,
// where it sits, what it may cost, who it is for and what it beats are answered here rather than
// distributed between an assembly method, a settings file and the prose of the prompt itself.
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

    // What one agent's whole system prompt may cost. It is re-sent on every request of every
    // conversation, and what it does not take is what the conversation itself gets — so this is a
    // ceiling on the static prefix rather than on the context window, and the sum of the budgets
    // below has to stay under it or the table is decoration.
    public const int MaxAgentPromptTokens = 20_000;

    public static IReadOnlyList<PromptDeclaration> Declarations { get; } =
    [
        new()
        {
            Name = CoreDirective,
            Purpose = "The agent assists rather than gatekeeps; it does not refuse or hedge a request the user owns.",
            Priority = PromptPriority.CoreDirective,
            TokenBudget = 250,
            Conflict = ConflictPolicy.Governs(PromptRules.Refusals)
        },
        new()
        {
            Name = Identity,
            Purpose = "Which agent this is, before any tool or feature prompt is read.",
            Priority = PromptPriority.Identity,
            TokenBudget = 100
        },
        new()
        {
            Name = UserContext,
            Purpose = "The user every user-scoped tool call in this conversation belongs to.",
            Priority = PromptPriority.UserContext,
            TokenBudget = 100
        },
        new()
        {
            Name = Subagents,
            Purpose = "When to delegate to a worker, and how to answer from what it returns.",
            Priority = PromptPriority.Feature,
            TokenBudget = 600,
            Conflict = ConflictPolicy.Governs(
                PromptRules.ToolUse, PromptRules.Formatting, PromptRules.Verbosity),
            Claims = SubAgentPrompt.Claims
        },
        new()
        {
            Name = Memory,
            Purpose = "Memory is invisible plumbing: applied silently, never narrated, forgotten on request.",
            Priority = PromptPriority.Feature,
            TokenBudget = 500,
            Conflict = ConflictPolicy.Governs(PromptRules.Memory)
        },
        new()
        {
            Name = FilesystemMounts,
            Purpose = "The mounts this session actually has, and which one a path belongs under.",
            Priority = PromptPriority.FileSystem,
            TokenBudget = 500,
            // The words are generated from the registry, so the claims live beside the code that
            // builds them rather than in a prompt file of their own.
            Claims = FileSystemToolFeature.Claims
        },
        new()
        {
            Name = SandboxPrompt.Name,
            Purpose = "The sandbox's layout, what may be run in it and what it refuses.",
            Priority = PromptPriority.Client,
            TokenBudget = 1_400,
            ServedBy = "mcp-sandbox"
        },
        new()
        {
            Name = VaultPrompt.Name,
            Purpose = "Obsidian's conventions: frontmatter, wikilinks, where a new note belongs.",
            Priority = PromptPriority.Client,
            TokenBudget = 2_000,
            ServedBy = "mcp-vault",
            Claims = VaultPrompt.Claims
        },
        new()
        {
            Name = WebBrowsingPrompt.Name,
            Purpose = "Searching, loading and reading pages, and what to do when one refuses.",
            Priority = PromptPriority.Client,
            TokenBudget = 1_500,
            ServedBy = "mcp-websearch"
        },
        new()
        {
            Name = DownloaderPrompt.Name,
            Purpose = "Finding and fetching media, and the download assistant's persona.",
            Priority = PromptPriority.Client,
            TokenBudget = 4_000,
            ServedBy = "mcp-library",
            Conflict = ConflictPolicy.Governs(PromptRules.Formatting, PromptRules.Verbosity)
        },
        new()
        {
            Name = IdealistaPrompt.Name,
            Purpose = "Property search: the filters that exist and how a listing is read back.",
            Priority = PromptPriority.Client,
            TokenBudget = 600,
            ServedBy = "mcp-idealista"
        },
        new()
        {
            Name = HomeAssistantPrompt.Name,
            Purpose = "The house: its areas and entities, and how an intent becomes a service call.",
            Priority = PromptPriority.Client,
            // The largest section by far, and the only one that grows with the deployment rather
            // than with an edit: the setup index naming every area and entity is appended to it
            // when the server serves it.
            TokenBudget = 5_000,
            ServedBy = "mcp-homeassistant",
            Claims = HomeAssistantPrompt.Claims
        },
        new()
        {
            Name = SchedulingPrompt.Name,
            Purpose = "Scheduled tasks as files: creating, listing, editing and firing one.",
            Priority = PromptPriority.Client,
            TokenBudget = 2_000,
            ServedBy = "mcp-scheduling"
        },
        new()
        {
            Name = PrintingPrompt.Name,
            Purpose = "The printer's queue, the formats it takes and what it rejects.",
            Priority = PromptPriority.Client,
            TokenBudget = 600,
            ServedBy = "mcp-printer"
        },
        new()
        {
            Name = TimerPrompt.Name,
            Purpose = "Timers and alarms on the satellites, named by the room they ring in.",
            Priority = PromptPriority.Client,
            TokenBudget = 1_500,
            ServedBy = "mcp-timers",
            Claims = TimerPrompt.Claims
        },
        new()
        {
            Name = Date,
            Purpose = "Today's date. Last of the static sections, because it is the only one that changes on its own.",
            Priority = PromptPriority.Date,
            TokenBudget = 30
        },
        new()
        {
            Name = CustomInstructions,
            Purpose = "Per-agent configuration, read closest to the conversation.",
            Priority = PromptPriority.CustomInstructions,
            TokenBudget = 800
        },
        new()
        {
            Name = VoicePrompt.Name,
            Purpose = "Every reply is spoken aloud, so it is one short sentence of plain words.",
            Priority = PromptPriority.ChannelOverride,
            TokenBudget = 800,
            Audience = PromptAudience.ForChannels("voice"),
            // The prose says it too, in its last paragraph. Both statements are about the same two
            // sections: the ones above that tell a screen-reader how to shape and how long to make
            // an answer.
            Conflict = ConflictPolicy
                .Governs(PromptRules.Formatting, PromptRules.Verbosity, PromptRules.ToolUse)
                .Beating(Subagents, DownloaderPrompt.Name),
            Claims = VoicePrompt.Claims
        },
        new()
        {
            Name = Language,
            Purpose = "The reply language, stated absolutely, against a request that is otherwise all English.",
            Priority = PromptPriority.Language,
            TokenBudget = 300,
            Conflict = ConflictPolicy.Governs(PromptRules.Language)
        }
    ];

    public const string CoreDirective = "core_directive";
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

    // Sections whose words live in this repo and are chosen by name in an agent's configuration.
    // Everything else is bound by whoever produces its text.
    private static readonly Dictionary<string, string> _selectable =
        new(StringComparer.OrdinalIgnoreCase) { [VoicePrompt.Name] = VoicePrompt.Instructions };

    public static IReadOnlyCollection<string> SelectableSections => _selectable.Keys;

    // Aggregated across sections the way the declarations themselves are, so a scenario can cite
    // one id and a coverage test can enumerate every claim the deployment makes.
    public static IReadOnlyList<PromptClaim> Claims { get; } =
        [.. Declarations.SelectMany(d => d.Claims)];

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

    private static PromptDeclaration Undeclared(string name) => new()
    {
        Name = name,
        Purpose = "Served by an MCP server this deployment has not declared.",
        Priority = PromptPriority.Client,
        TokenBudget = UndeclaredBudget,
        Declared = false
    };
}