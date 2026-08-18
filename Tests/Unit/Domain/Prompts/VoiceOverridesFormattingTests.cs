using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// Nabu's answers are spoken, and almost everything else in its prompt was written for a reply
// somebody reads: lead with the conclusion, use a list, say what you searched. Those sections are
// not wrong — they are wrong out loud — so the voice rules have to beat them, and beating them is
// a position in the prompt rather than an opinion in it.
public class VoiceOverridesFormattingTests
{
    private static PromptAssembly Nabu => AgentPromptFixture.Assemble("nabu");

    [Fact]
    public void Nabu_ReadsTheVoiceRules()
    {
        Nabu.Sections.Select(s => s.Name).ShouldContain(VoicePrompt.Name);
    }

    // The conflict is real rather than theoretical: one section shapes a written answer, the other
    // forbids the shapes. If either of these stops being true the override has nothing left to do
    // and should be reconsidered rather than quietly kept.
    [Fact]
    public void TheSectionsInConflict_ReallyDoLegislateTheSameThing()
    {
        SubAgentPrompt.SystemPrompt.ShouldContain("In a written reply");
        VoicePrompt.Instructions.ShouldContain("No emojis, markdown, bullet points");
    }

    [Fact]
    public void TheVoiceRules_AreReadAfterEverySectionTheyOverride()
    {
        var order = Nabu.Sections.Select(s => s.Name).ToList();
        var voice = order.IndexOf(VoicePrompt.Name);

        var overridden = PromptManifest.Find(VoicePrompt.Name)!.Conflict.Overrides
            .Where(order.Contains)
            .ToList();

        overridden.ShouldNotBeEmpty("the override beats nothing this agent actually reads");
        foreach (var name in overridden)
        {
            voice.ShouldBeGreaterThan(order.IndexOf(name), $"voice must be read after '{name}'");
        }
    }

    // The same statement twice, in the two forms it has to hold in: the declaration the assembly
    // checks, and the paragraph the model reads. Losing either one leaves the other saying
    // something nobody enforces.
    [Fact]
    public void TheOverride_IsDeclaredAndAlsoStatedToTheModel()
    {
        var conflict = PromptManifest.Find(VoicePrompt.Name)!.Conflict;

        conflict.Claims.ShouldContain(PromptRules.Formatting);
        conflict.Claims.ShouldContain(PromptRules.Verbosity);
        conflict.Overrides.ShouldContain(PromptManifest.Subagents);

        VoicePrompt.Instructions.ShouldContain("these rules win");
    }

    // The reply language is the one thing the voice rules do not touch, and it is stated in the
    // language it is about — so it stays last, after the rules that decide what is said.
    [Fact]
    public void TheVoiceRules_AreStillReadBeforeTheLanguageRule()
    {
        var order = Nabu.Sections.Select(s => s.Name).ToList();

        order.IndexOf(VoicePrompt.Name).ShouldBeLessThan(order.IndexOf(PromptManifest.Language));
    }

    // Nothing in nabu's prompt legislates the same rule twice without an answer. This is the check
    // that fails when a new section arrives claiming formatting or verbosity and nobody decided
    // whether the voice rules still win.
    [Fact]
    public void Nabu_AssemblesWithNoUnresolvedContradiction()
    {
        Nabu.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void AnAgentThatIsNotSpoken_DoesNotReadTheVoiceRules()
    {
        AgentPromptFixture.Assemble("jonas").Sections
            .Select(s => s.Name)
            .ShouldNotContain(VoicePrompt.Name);
    }

    // The section declares the channel it belongs to, and the routing default for that channel
    // names the agent that reads it. Two files, one statement — and a voice channel pointed at an
    // agent with no voice rules would be a satellite reading paragraphs aloud.
    [Fact]
    public void TheChannelTheVoiceRulesDeclare_RoutesToAnAgentThatSelectsThem()
    {
        var channel = PromptManifest.Find(VoicePrompt.Name)!.Audience.Channels.ShouldHaveSingleItem();
        var defaults = AgentPromptFixture.AgentDefaults;

        var agentId = defaults.For(channel).ShouldNotBeNull();
        AgentPromptFixture.Agents.Single(a => a.Id == agentId)
            .PromptSections.ShouldContain(VoicePrompt.Name);
    }
}