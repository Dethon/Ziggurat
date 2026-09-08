using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.Configuration;
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
            [SandboxPrompt.Name] = SandboxPrompt.Build("/sandbox", "home/sandbox_user"),
            [VaultPrompt.Name] = VaultPrompt.Prompt,
            [WebBrowsingPrompt.Name] = WebBrowsingPrompt.AgentSystemPrompt,
            [DownloaderPrompt.Name] = DownloaderPrompt.AgentSystemPrompt,
            [IdealistaPrompt.Name] = IdealistaPrompt.SystemPrompt,
            [HomeAssistantPrompt.Name] = HomeAssistantPrompt.SystemPrompt,
            [SchedulingPrompt.Name] = SchedulingPrompt.Prompt,
            [PrintingPrompt.Name] = PrintingPrompt.Build("text,jpeg"),
            [TimerPrompt.Name] = TimerPrompt.Build([])
        };

    // What each server ships as skills, bound to the manifest exactly as the client manager binds
    // what it reads off the wire: the served description and the served body, under the declaration.
    public static IReadOnlyDictionary<string, PromptSkill> ServedSkills { get; } =
        new[] { HomeWatchesSkill.Text, HomeAssistantSkill.Text, ObsidianVaultSkill.Text, WebBrowsingSkill.Text, SchedulingSkill.For("Europe/Madrid"), SandboxSkill.For("/sandbox", "home/sandbox_user") }
            .ToDictionary(
                text => text.Name,
                text => PromptManifest.BindSkill(text.Name, text.Description, text.Body),
                StringComparer.OrdinalIgnoreCase);

    // The feature prompts, keyed by the feature name that turns them on — the same key the manifest
    // declares them under.
    public static IReadOnlyDictionary<string, string> FeatureText { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PromptManifest.Subagents] = SubAgentPrompt.SystemPrompt,
            [PromptManifest.Memory] = MemoryPrompts.FeatureSystemPrompt
        };

    public static IReadOnlyList<AgentDefinition> Agents { get; } =
        Config().GetSection("agents").Get<AgentDefinition[]>()!;

    public static IReadOnlyList<SubAgentDefinition> SubAgents { get; } =
        Config().GetSection("subAgents").Get<SubAgentDefinition[]>()!;

    public static AgentDefaults AgentDefaults { get; } =
        Config().GetSection("agentDefaults").Get<AgentDefaults>() ?? new AgentDefaults();

    // Every agent and worker a snapshot is kept for: the two people talk to, and the worker the
    // delegating ones spawn.
    public static IReadOnlyList<string> SnapshotIds { get; } = ["jonas", "nabu", "jonas-worker"];

    public static PromptAssembly Assemble(string id)
    {
        var agent = Agents.FirstOrDefault(a => a.Id == id);
        var worker = SubAgents.FirstOrDefault(a => a.Id == id);

        return agent is not null
            ? Compose(
                agent.Id, agent.Name, agent.Description, agent.McpServerEndpoints,
                agent.EnabledFeatures, agent.PromptSections, agent.CustomInstructions, agent.Language)
            : worker is not null
                ? Compose(
                    worker.Id, worker.Name, worker.Description, worker.McpServerEndpoints,
                    worker.EnabledFeatures, worker.PromptSections, worker.CustomInstructions,
                    worker.Language)
                : throw new InvalidOperationException($"No agent or subagent '{id}' in appsettings.json");
    }

    private static PromptAssembly Compose(
        string id,
        string name,
        string? description,
        IEnumerable<string> endpoints,
        IEnumerable<string> features,
        IEnumerable<string> selected,
        string? customInstructions,
        string? language) =>
        PromptComposer.Compose(new PromptContext
        {
            AgentId = id,
            Name = name,
            Description = description,
            Domain = [.. FeatureSections(features)],
            FileSystem = HasFilesystem(features)
                ? [PromptManifest.Bind(PromptManifest.FilesystemMounts, SampleMounts)]
                : [],
            Client =
            [
                PromptManifest.Bind(PromptManifest.UserContext, SampleUserContext),
                .. ServedSections(endpoints)
            ],
            Skills = [.. SkillsOf(endpoints)],
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

    // The skills arrive with the servers, so an agent has exactly the skills of the services it dials.
    public static IEnumerable<PromptSkill> SkillsOf(IEnumerable<string> endpoints) =>
        endpoints
            .Select(ServiceOf)
            .SelectMany(service => PromptManifest.Skills.Where(s => s.ServedBy == service))
            .Select(s => ServedSkills[s.Name]);

    public static string ServiceOf(string endpoint) => new Uri(endpoint).Host;

    public static string SkillSnapshotPath(string name) =>
        Path.Combine(McpServerRegistrations.RepoRoot, "Tests", "Snapshots", $"skill.{name}.md");

    public static string SnapshotPath(string id) =>
        Path.Combine(McpServerRegistrations.RepoRoot, "Tests", "Snapshots", $"prompt.{id}.txt");

    private static IConfigurationRoot Config() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(McpServerRegistrations.RepoRoot, "Agent", "appsettings.json"))
            .Build();
}