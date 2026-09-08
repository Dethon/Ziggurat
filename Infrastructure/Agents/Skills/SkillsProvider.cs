using System.Text.Json;
using Domain.Prompts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.Skills;

// The framework's skills provider, attached the way this repo needs it: the skills come from the
// session the turn runs in, the template is the bare list the base prompt's `skills` section
// explains, the load tool needs no approval, and the resource and script tools are not offered
// because no skill here ships a resource or a script (docs/adr/0039).
//
// A wrapper rather than the provider itself because the provider always builds all three tools
// and has no switch for the two nothing needs; what reaches the model is what leaves here. The
// load tool itself is re-faced at the same seam: the framework's description is generic and its
// `skillName` is free text, and the model was seen calling it as a warm-up probe with garbage
// names. Ours says when not to call it and lists the session's names as an enum, so a name that
// is not advertised cannot be sent.
public sealed class SkillsProvider : AIContextProvider, IDisposable
{
    // The whole of the template. The prose about what the list is and when to load from it is a
    // declared section of the base prompt, budgeted and snapshotted; the framework only appends
    // the list, so the snapshot renders through this same provider to show it.
    public const string Template = "{skills}";

    public static readonly string LoadToolName = AgentSkillsProvider.LoadSkillToolName;

    public const string LoadToolDescription =
        "Loads one skill from the `<available_skills>` list. Call it only when the request is of that skill's kind, once per conversation, never to check what it does or to fill a pause.";

    private const string SkillNameParameter = "skillName";

    private readonly Func<AgentSession?, IReadOnlyList<PromptSkill>> _skillsOf;
    private readonly AgentSkillsProvider _inner;

    public SkillsProvider(Func<AgentSession?, IReadOnlyList<PromptSkill>> skillsOf)
    {
        _skillsOf = skillsOf;
        _inner = new AgentSkillsProvider(
            new SessionSkillsSource(skillsOf),
            new AgentSkillsProviderOptions
            {
                SkillsInstructionPrompt = Template,
                DisableLoadSkillApproval = true
            },
            ownsSource: true);
    }

    // What the model sees for a skill: the framework's inline skill, whose body a load returns
    // wrapped in the framework's own tags.
    public static AgentInlineSkill Inline(PromptSkill skill) =>
        new(skill.Name, skill.Description, skill.Body);

#pragma warning disable MAAI001 // The invoking context is the only way to drive a provider by hand.
    // The advertisement alone, rendered by the framework: what is appended to the instructions of
    // an agent that has these skills. The snapshot tests call this so the whole prompt is on file.
    public static async Task<string> AdvertisementAsync(
        IReadOnlyList<PromptSkill> skills, AIAgent agent, CancellationToken ct = default)
    {
        using var provider = new SkillsProvider(_ => skills);
        var context = await provider.InvokingAsync(new InvokingContext(agent, null, new AIContext()), ct);
        return context.Instructions ?? string.Empty;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var provided = await _inner.InvokingAsync(
            new InvokingContext(context.Agent, context.Session, new AIContext()), cancellationToken);

        var load = provided.Tools?
            .OfType<AIFunction>()
            .FirstOrDefault(t => string.Equals(t.Name, LoadToolName, StringComparison.Ordinal));

        return new AIContext
        {
            Instructions = provided.Instructions,
            Tools = load is null
                ? null
                : [new LoadSkillFunction(load, [.. _skillsOf(context.Session).Select(s => s.Name)])]
        };
    }
#pragma warning restore MAAI001

    // The framework's load, invoked as the framework wrote it, behind a face of our own.
    private sealed class LoadSkillFunction(AIFunction inner, IReadOnlyList<string> names) : DelegatingAIFunction(inner)
    {
        public override string Description => LoadToolDescription;

        public override JsonElement JsonSchema { get; } = SchemaOver(names);

        private static JsonElement SchemaOver(IReadOnlyList<string> names)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    [SkillNameParameter] = new
                    {
                        type = "string",
                        description = "The name of the skill to load, exactly as listed.",
                        @enum = names
                    }
                },
                required = new[] { SkillNameParameter },
                additionalProperties = false
            }));
            return document.RootElement.Clone();
        }
    }

    public void Dispose() => _inner.Dispose();

    // The skills of whichever session the turn runs in. An agent outlives many sessions and each
    // session dials its own servers, so the skills are the session's, asked for per turn.
    private sealed class SessionSkillsSource(Func<AgentSession?, IReadOnlyList<PromptSkill>> skillsOf)
        : AgentSkillsSource
    {
        public override Task<IList<AgentSkill>> GetSkillsAsync(
            AgentSkillsSourceContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IList<AgentSkill>>([.. skillsOf(context.Session).Select(Inline)]);
    }
}