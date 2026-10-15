using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// A server's prompt goes to every agent that dials the server, unless its declaration names the
// agents it is for. That is how one server can serve two agents different things: Sugary's
// estimating words are the sugary agent's, while Jonas dials the same server for her log.
public class PromptAudienceTests
{
    [Fact]
    public void EveryAgentAnAudienceNames_IsConfigured()
    {
        var configured = AgentPromptFixture.Agents.Select(a => a.Id)
            .Concat(AgentPromptFixture.SubAgents.Select(w => w.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var declaration in PromptManifest.Declarations)
        {
            declaration.Audience.Agents
                .Where(agent => !configured.Contains(agent))
                .ShouldBeEmpty($"'{declaration.Name}' is for an agent nobody configured");
        }
    }

    [Fact]
    public void TheEstimatingWords_ReachTheSugaryAgentAlone_ThoughJonasDialsTheirServer()
    {
        var jonas = AgentPromptFixture.Assemble("jonas");
        var sugary = AgentPromptFixture.Assemble("sugary");

        jonas.Skills.Select(s => s.Name).ShouldContain(SugarySkill.Name);
        jonas.Sections.Select(s => s.Name).ShouldNotContain(SugaryPrompt.Name);
        sugary.Sections.Select(s => s.Name).ShouldContain(SugaryPrompt.Name);
    }
}