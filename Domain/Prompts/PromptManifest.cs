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

    // What a skill nobody declared may cost, description and body, before somebody has thought
    // about it — the same rule as an undeclared prompt, for the same kind of server.
    public const int UndeclaredSkillDescriptionBudget = 100;
    public const int UndeclaredSkillBodyBudget = 1_500;

    // What the largest agent's standing sections are budgeted to, rounded up to the hundred. It is
    // a ratchet, not a limit: every section that moves behind a skill lowers it by editing this one
    // number, and the budget tests refuse a figure left where it was, so what left the base prompt
    // cannot grow back into the room it vacated.
    public const int StandingTokens = 8_600;

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

    public static IReadOnlyList<PromptDeclaration> Declarations { get; } =
    [
        new()
        {
            Name = CoreDirective,
            Purpose = "The agent assists rather than gatekeeps; it does not refuse or hedge a request the user owns, and it calls a tool only when it needs its answer.",
            Priority = PromptPriority.CoreDirective,
            // 250 covered the refusals prose alone; the tool-call rule landed exactly on it,
            // leaving a section every agent reads one word from failing the build. 400 for the
            // irreversible-change carve-out: it is the one exception to "never hedge", and the
            // words that keep a model from reading it as stop-and-wait are the words that earn
            // its place — seven of the user's notes went the turn it was missing.
            TokenBudget = 400,
            Conflict = ConflictPolicy.Governs(PromptRules.Refusals),
            Claims = CoreDirectivePrompt.Claims
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
            Conflict = ConflictPolicy.Governs(PromptRules.Memory),
            Claims = MemoryPrompts.Claims
        },
        new()
        {
            Name = SkillsSection,
            Purpose = "What the advertised skill list is, and that a skill is loaded once, before acting, and only when a request calls for it.",
            Priority = PromptPriority.Feature,
            TokenBudget = 250
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
            Purpose = "That the sandbox is the one mount with exec, where its workspace is, and that a run loads the skill.",
            Priority = PromptPriority.Client,
            // The stub: the section had no choosing rule of its own and moved whole into the
            // sandbox skill. Ratcheted from 1,400.
            TokenBudget = 250,
            ServedBy = "mcp-sandbox"
        },
        new()
        {
            Name = VaultPrompt.Name,
            Purpose = "What the vault is, and that a task needing exec is transferred to the sandbox once.",
            Priority = PromptPriority.Client,
            // The stub: the conventions are the obsidian-vault skill. Ratcheted from 2,000.
            TokenBudget = 400,
            ServedBy = "mcp-vault",
            Claims = VaultPrompt.Claims
        },
        new()
        {
            Name = WebBrowsingPrompt.Name,
            Purpose = "That the browser exists, that a url comes from a search, and that no call is a probe.",
            Priority = PromptPriority.Client,
            // The stub: the workflow is the web-browsing skill. Ratcheted from 1,500.
            TokenBudget = 400,
            ServedBy = "mcp-websearch",
            Claims = WebBrowsingPrompt.Claims
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
            Purpose = "The house: which mechanism a request is, and that a home task starts by loading the skill and reading the setup index.",
            Priority = PromptPriority.Client,
            // The stub of what was the largest section: the setup index is a file the mount
            // serves, the watches are a skill, and the doing rules are the home-assistant skill.
            // Ratcheted from 6,500 through 5,000, 4,500 and 1,200 as each of them left, the last
            // when the mechanism rule moved to its one statement under Timers.
            TokenBudget = 1_000,
            ServedBy = "mcp-homeassistant",
            Claims = HomeAssistantPrompt.Claims
        },
        new()
        {
            Name = SchedulingPrompt.Name,
            Purpose = "That a schedule is a deferred action of the agent's own, never a human reminder, and that writing one loads the skill.",
            Priority = PromptPriority.Client,
            // The stub plus the live agent list the server appends: the file's rules are the
            // scheduling skill. Ratcheted from 2,000, then from 700 when the mechanism rule moved
            // to its one statement under Timers.
            TokenBudget = 500,
            ServedBy = "mcp-scheduling",
            Claims = SchedulingPrompt.Claims
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
            Purpose = "Which mechanism a moment is — a countdown, a calendar alarm, a scheduled task — and the live satellite roster.",
            Priority = PromptPriority.Client,
            // The stub plus the roster the server appends: the file's rules are the
            // countdown-timers skill. Ratcheted from 1,500.
            TokenBudget = 900,
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

    // Every skill a server in this deployment ships, declared beside the sections. A skill is read
    // on demand, so its body's budget is what one load costs a conversation and its description's
    // budget is what every turn pays to advertise it.
    public static IReadOnlyList<SkillDeclaration> Skills { get; } =
    [
        new()
        {
            Name = HomeWatchesSkill.Name,
            Description = HomeWatchesSkill.Description,
            DescriptionBudget = 120,
            BodyBudget = 2_200,
            ServedBy = "mcp-homeassistant",
            Claims = HomeWatchesSkill.Claims
        },
        new()
        {
            Name = HomeAssistantSkill.Name,
            Description = HomeAssistantSkill.Description,
            // The exclusions are what keep it off timer and watch turns; the first gate showed
            // each one earning its words.
            DescriptionBudget = 150,
            BodyBudget = 3_600,
            ServedBy = "mcp-homeassistant",
            Claims = HomeAssistantSkill.Claims
        },
        new()
        {
            Name = ObsidianVaultSkill.Name,
            Description = ObsidianVaultSkill.Description,
            DescriptionBudget = 120,
            BodyBudget = 1_800,
            ServedBy = "mcp-vault",
            Claims = ObsidianVaultSkill.Claims
        },
        new()
        {
            Name = WebBrowsingSkill.Name,
            Description = WebBrowsingSkill.Description,
            DescriptionBudget = 130,
            // Raised from 1,400 when the web tools' descriptions gave up the snippet rule, the
            // browse options, the other action verbs and the force essay: ~700 tokens that every
            // request of every conversation paid, now ~250 paid once by a conversation that browses.
            BodyBudget = 1_600,
            ServedBy = "mcp-websearch",
            Claims = WebBrowsingSkill.Claims
        },
        new()
        {
            Name = SchedulingSkill.Name,
            Description = SchedulingSkill.Description,
            DescriptionBudget = 130,
            BodyBudget = 1_300,
            ServedBy = "mcp-scheduling",
            Claims = SchedulingSkill.Claims
        },
        new()
        {
            Name = SandboxSkill.Name,
            Description = SandboxSkill.Description,
            DescriptionBudget = 130,
            BodyBudget = 1_200,
            ServedBy = "mcp-sandbox",
            Claims = SandboxSkill.Claims
        },
        new()
        {
            Name = CountdownTimersSkill.Name,
            Description = CountdownTimersSkill.Description,
            DescriptionBudget = 130,
            BodyBudget = 900,
            ServedBy = "mcp-timers",
            Claims = CountdownTimersSkill.Claims
        }
    ];

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

    // Sections whose words live in this repo and are chosen by name in an agent's configuration.
    // Everything else is bound by whoever produces its text.
    private static readonly Dictionary<string, string> _selectable =
        new(StringComparer.OrdinalIgnoreCase) { [VoicePrompt.Name] = VoicePrompt.Instructions };

    public static IReadOnlyCollection<string> SelectableSections => _selectable.Keys;

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