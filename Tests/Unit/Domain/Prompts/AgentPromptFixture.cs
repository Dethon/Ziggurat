using Domain.Contracts;
using Domain.DTOs;
using Domain.Outposts;
using Domain.Prompts;
using Domain.Tools.Files;
using Domain.Tools.FileSystem;
using Moq;
using Tests.Integration.McpServers;

namespace Tests.Unit.Domain.Prompts;

// Assembles the real system prompt of a configured agent, without a running deployment.
//
// Half of a prompt is served by MCP servers at session warmup, so nothing in a unit test can fetch
// it. What it can do is bind each declaration to the text this repo hands that server, which is the
// same text — `McpServerScheduling` serves `SchedulingPrompt.Prompt` and nothing else. The
// samples below are therefore the source of truth for what a server will serve, and the parameters
// they are built with are fixed here so a snapshot moves only when a prompt does.
internal static class AgentPromptFixture
{
    // Fixed so nothing in a snapshot is a clock or a machine.
    public static readonly DateTimeOffset Now = new(2026, 5, 15, 10, 30, 0, TimeSpan.Zero);

    private const string SampleMounts =
        """
        ## Available Filesystems

        All `domain__filesystem__*` tool paths must start with one of these mount prefixes.

        - `/vault` — the user's Obsidian vault.
        - `/sandbox` — the sandbox container's disk.
        """;

    // An agent that opted into outposts sees the machines live at session build, under words the
    // filesystem feature generates. One machine stands in for them, described by the outpost's own
    // generator, so the snapshot holds the real prose about machines rather than a paraphrase.
    // A session whose sandbox serves its other mounts says so under the mounts, in the words the
    // filesystem feature generates from them; the sample is the vault and the sandbox above.
    private static readonly string _sampleShell = FileSystemToolFeature.ShellSection([
        new FileSystemMount("vault", "/vault", "the user's Obsidian vault."),
        new FileSystemMount("sandbox", "/sandbox", "the sandbox container's disk.")
        {
            ShellReach = ShellReach.Contained,
            OccupiedNames = ["app", "bin", "etc", "home", "sandbox", "usr", "vfs"]
        }
    ]);

    private static readonly string _sampleMachines = FileSystemToolFeature.MachinesSection([
        new FileSystemMount(
            "laptop",
            OutpostMountPoint.For("laptop"),
            new OutpostFileSystem("laptop", Mock.Of<IFileSystemClient>(), "/home/someone", [".md"]).DescribeMount)
    ]);

    private const string SampleUserContext =
        """
        ## User Context
        Conversation created by user: 'someone'
        Use this userId/username for all user-scoped operations. unless you get more updated information in the user's message
        """;

    // What each server serves, built with fixed arguments. A server whose prompt is parameterised
    // by the deployment (a time zone, a printer's formats, the satellites that exist) is pinned to
    // one set here; the shape is what a snapshot is holding still, not the deployment.
    public static IReadOnlyDictionary<string, string> ServedText { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // As the deployment builds it: a launcher and a bridge, so commands see the mounts.
            [SandboxPrompt.Name] = SandboxPrompt.Build("/sandbox", "home/sandbox_user", servesMounts: true),
            [VaultPrompt.Name] = VaultPrompt.Prompt,
            [WebBrowsingPrompt.Name] = WebBrowsingPrompt.AgentSystemPrompt,
            [DownloaderPrompt.Name] = DownloaderPrompt.AgentSystemPrompt,
            [LibraryGuide.Name] = LibraryGuide.Text,
            [IdealistaPrompt.Name] = IdealistaPrompt.SystemPrompt,
            [HomeAssistantPrompt.Name] = HomeAssistantPrompt.SystemPrompt,
            [SchedulingPrompt.Name] = SchedulingPrompt.Prompt,
            [PrintingPrompt.Name] = PrintingPrompt.Build("text,jpeg"),
            [TimerPrompt.Name] = TimerPrompt.Build([]),
            [SugaryPrompt.Name] = SugaryPrompt.Prompt
        };

    // What each server ships as skills, bound to the manifest exactly as the client manager binds
    // what it reads off the wire: the served description and the served body, under the declaration.
    // A server's own skills in the order its registration lists them (the home's watches first).
    private static readonly IReadOnlyList<PromptSkill> _servedInOrder =
    [
        .. new[] { HomeWatchesSkill.Text, HomeAssistantSkill.Text, ObsidianVaultSkill.Text, WebBrowsingSkill.Text, SchedulingSkill.Text, SandboxSkill.For("/sandbox", "home/sandbox_user", servesMounts: true), CountdownTimersSkill.Text, SugarySkill.Text }
            .Select(text => PromptManifest.BindSkill(text.Name, text.Description, text.Body))
    ];

    public static IReadOnlyDictionary<string, PromptSkill> ServedSkills { get; } =
        _servedInOrder.ToDictionary(skill => skill.Name, StringComparer.OrdinalIgnoreCase);

    // The feature prompts, keyed by the feature name that turns them on — the same key the manifest
    // declares them under.
    public static IReadOnlyDictionary<string, string> FeatureText { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PromptManifest.Subagents] = SubAgentPrompt.SystemPrompt,
            [PromptManifest.Memory] = MemoryPrompts.FeatureSystemPrompt
        };

    // The tests' own copies of the agents, not the agent's settings file: a snapshot holds today's
    // definitions still, whatever a household is configured with.
    public static IReadOnlyList<AgentDefinition> Agents => TestAgents.Repository.Agents;

    public static IReadOnlyList<SubAgentDefinition> SubAgents => TestAgents.Repository.SubAgents;

    public static AgentDefaults AgentDefaults => TestAgents.Repository.Defaults;

    // A shipped template's snapshot id, kept apart from the home's agent of the same name.
    private const string TemplatePrefix = "template.";

    // Every agent and worker a snapshot is kept for: the two people talk to, the worker the
    // delegating ones spawn, the one her carb-counting app reaches, and the voice template, whose
    // prompt is what every household's agent that answers aloud is told — the home's Nabu is not.
    public static IReadOnlyList<string> SnapshotIds { get; } =
        ["jonas", "nabu", "jonas-worker", "sugary", TemplatePrefix + AgentTemplates.Nabu];

    public static PromptAssembly Assemble(string id)
    {
        // As a household runs it before its owner said anything: the template's own name, no custom
        // instructions, and no language, which is the household's.
        var agent = id.StartsWith(TemplatePrefix, StringComparison.Ordinal)
            ? AgentTemplates.Named(id[TemplatePrefix.Length..])
            : Agents.FirstOrDefault(a => a.Id == id);
        var worker = SubAgents.FirstOrDefault(a => a.Id == id);

        return agent is not null
            ? Compose(
                agent.Id, agent.Name, agent.Description, agent.McpServerEndpoints,
                agent.EnabledFeatures, agent.PromptSections, agent.CustomInstructions, agent.Language,
                agent.UsesOutposts, agent.NoTools)
            : worker is not null
                ? Compose(
                    worker.Id, worker.Name, worker.Description, worker.McpServerEndpoints,
                    worker.EnabledFeatures, worker.PromptSections, worker.CustomInstructions,
                    worker.Language, worker.UsesOutposts, noTools: false)
                : throw new InvalidOperationException($"No agent or subagent '{id}' in {TestAgents.Repository.Path}");
    }

    private static PromptAssembly Compose(
        string id,
        string name,
        string? description,
        IEnumerable<string> endpoints,
        IEnumerable<string> features,
        IEnumerable<string> selected,
        string? customInstructions,
        string? language,
        bool usesOutposts,
        bool noTools) =>
        PromptComposer.Compose(new PromptContext
        {
            AgentId = id,
            Name = name,
            Description = description,
            Domain = [.. FeatureSections(features)],
            FileSystem = HasFilesystem(features)
                ? [PromptManifest.Bind(
                    PromptManifest.FilesystemMounts,
                    string.Join("\n\n", new[]
                    {
                        SampleMounts,
                        usesOutposts ? _sampleMachines : null,
                        endpoints.Any(e => ServiceOf(e) == "mcp-sandbox") ? _sampleShell : null
                    }.OfType<string>()))]
                : [],
            Client =
            [
                PromptManifest.Bind(PromptManifest.UserContext, SampleUserContext),
                .. ServedSections(endpoints)
            ],
            // An agent that calls nothing is dialled for its servers' prompts alone, and is offered
            // none of their skills.
            Skills = noTools ? [] : [.. SkillsOf(endpoints)],
            Selected = [.. selected.Select(s => PromptManifest.Selected(s)!)],
            CustomInstructions = customInstructions,
            Language = language,
            Now = Now
        });

    private static IEnumerable<PromptSection> FeatureSections(IEnumerable<string> features) =>
        features
            .Select(f => f.Split('.', 2)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(FeatureText.ContainsKey)
            .Select(f => PromptManifest.Bind(f, FeatureText[f]));

    private static bool HasFilesystem(IEnumerable<string> features) =>
        features.Any(f => f.Split('.', 2)[0].Equals("filesystem", StringComparison.OrdinalIgnoreCase));

    // An endpoint is a compose service, and the manifest says which sections that service serves.
    public static IEnumerable<PromptSection> ServedSections(IEnumerable<string> endpoints) =>
        endpoints
            .Select(ServiceOf)
            .SelectMany(service => PromptManifest.Declarations.Where(d => d.ServedBy == service))
            .Select(d => d.Bind(ServedText[d.Name]));

    // The skills arrive with the servers, so an agent has exactly the skills of the services it dials,
    // in the order each server lists them in its index.
    public static IEnumerable<PromptSkill> SkillsOf(IEnumerable<string> endpoints) =>
        endpoints
            .Select(ServiceOf)
            .SelectMany(service => _servedInOrder.Where(s => s.Declaration.ServedBy == service));

    // A household's server, named rather than addressed, is the server the deployment ran under the
    // compose name the manifest declares it by (vault -> mcp-vault), which is what serves its prompts.
    public static string ServiceOf(string endpoint) =>
        global::Domain.DTOs.Tunnel.TunnelCredentialProtocol.IsServerName(endpoint)
            ? $"mcp-{endpoint}"
            : new Uri(endpoint).Host;

    public static string SkillSnapshotPath(string name) =>
        Path.Combine(McpServerRegistrations.RepoRoot, "Tests", "Snapshots", $"skill.{name}.md");

    public static string SnapshotPath(string id) =>
        Path.Combine(McpServerRegistrations.RepoRoot, "Tests", "Snapshots", $"prompt.{id}.txt");
}