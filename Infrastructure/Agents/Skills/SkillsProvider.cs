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
// and has no switch for the two nothing needs; what reaches the model is what leaves here.
public sealed class SkillsProvider : AIContextProvider, IDisposable
{
    // The whole of the template. The prose about what the list is and when to load from it is a
    // declared section of the base prompt, budgeted and snapshotted; the framework only appends
    // the list, so the snapshot renders through this same provider to show it.
    public const string Template = "{skills}";

    public static readonly string LoadToolName = AgentSkillsProvider.LoadSkillToolName;

    private readonly AgentSkillsProvider _inner;

    public SkillsProvider(Func<AgentSession?, IReadOnlyList<PromptSkill>> skillsOf)
        : this(new SessionSkillsSource(skillsOf))
    {
    }

    public SkillsProvider(IReadOnlyList<PromptSkill> skills)
        : this(new SessionSkillsSource(_ => skills))
    {
    }

    private SkillsProvider(AgentSkillsSource source)
    {
        _inner = new AgentSkillsProvider(
            source,
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
        using var provider = new SkillsProvider(skills);
        var context = await provider.InvokingAsync(new InvokingContext(agent, null, new AIContext()), ct);
        return context.Instructions ?? string.Empty;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var provided = await _inner.InvokingAsync(
            new InvokingContext(context.Agent, context.Session, new AIContext()), cancellationToken);

        return new AIContext
        {
            Instructions = provided.Instructions,
            Tools = provided.Tools?.Where(t => string.Equals(t.Name, LoadToolName, StringComparison.Ordinal)).ToList()
        };
    }
#pragma warning restore MAAI001

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