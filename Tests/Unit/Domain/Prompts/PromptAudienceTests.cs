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

    // A household's second agent of a template goes by the template's id and a number (nabu-2,
    // sugary-2), and is the same agent: an audience naming the template's id reached its first alone,
    // so a second Sugary estimated without its estimating words and a second Nabu fetched downloads
    // without the library's workings.
    [Theory]
    [InlineData("nabu", true)]
    [InlineData("nabu-2", true)]
    [InlineData("nabu-17", true)]
    [InlineData("NABU-3", true)]
    [InlineData("nabuko", false)]
    [InlineData("nabu-", false)]
    [InlineData("nabu-x", false)]
    [InlineData("jack", false)]
    public void AnAudience_IncludesTheAgentsAHouseholdMadeFromItsTemplate(string agentId, bool included)
    {
        PromptAudience.ForAgents("nabu").Includes(agentId).ShouldBe(included);
    }

    // A household's chat agent dials its kit's library too, and is not the home's download assistant.
    [Fact]
    public void TheLibrarysWorkings_ReachTheTemplatesAgents_AndNotTheDownloadAssistant()
    {
        var guide = PromptManifest.Declarations.Single(d => d.Name == LibraryGuide.Name).Audience;

        guide.Includes("nabu").ShouldBeTrue();
        guide.Includes("jonas-2").ShouldBeTrue();
        guide.Includes("jack").ShouldBeFalse();
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