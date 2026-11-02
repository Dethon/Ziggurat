using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Household;
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

// A household's agents as its turns run them (seam 3): each entry of its agents section a shipped
// template given the household's name and instructions for it, or a definition seeded for it; the
// household's language for an agent that pins none; its features withholding the shared servers and
// the domain features it turned off.
public class HouseholdAgentsTests
{
    private static readonly SharedServers _shared = new()
    {
        Scheduling = "http://mcp-scheduling:8080/mcp",
        Timers = "http://mcp-timers:8080/mcp",
        Sugary = "http://mcp-sugary:8080/mcp",
        Idealista = "http://mcp-idealista:8080/mcp"
    };

    [Fact]
    public void ATemplateEntry_IsTheTemplate_UnderTheHouseholdsIdNameAndInstructions()
    {
        var household = Household(new AgentsSection
        {
            DefaultAgent = "ana",
            Agents = [new HouseholdAgent { Id = "ana", Template = "jonas", Name = "Ana", CustomInstructions = "Rhyme." }]
        });

        var ana = HouseholdAgents.Of(household, _shared).Agents.ShouldHaveSingleItem();

        ana.Id.ShouldBe("ana");
        ana.Name.ShouldBe("Ana");
        ana.CustomInstructions.ShouldBe("Rhyme.");
        ana.Model.ShouldBe(AgentTemplates.Named("jonas")!.Model);
        ana.Description.ShouldBe(AgentTemplates.Named("jonas")!.Description);
    }

    [Fact]
    public void AHouseholdWhoseSectionNamesNoAgent_HasTheTwoTemplates_AndJonasAnswersByDefault()
    {
        var agents = HouseholdAgents.Of(Household(new AgentsSection()), _shared);

        agents.Agents.Select(a => (a.Id, a.Name)).ShouldBe([("jonas", "Jonas"), ("sugary", "Sugary")]);
        agents.DefaultAgent.ShouldBe("jonas");
        agents.Agents.Single(a => a.Id == "sugary").NoTools.ShouldBeTrue();
    }

    // The home's own agents are seeded whole, and run as they were defined.
    [Fact]
    public void ADefinitionEntry_IsItsDefinition()
    {
        var jack = new AgentDefinition
        {
            Id = "jack", Name = "Jack", Model = "m", McpServerEndpoints = ["library", "websearch"],
            EnabledFeatures = ["filesystem.read"]
        };

        var agents = HouseholdAgents.Of(Household(new AgentsSection
        {
            DefaultAgent = "jack", Agents = [new HouseholdAgent { Id = "jack", Definition = jack }]
        }), _shared);

        var built = agents.Agents.ShouldHaveSingleItem();
        built.McpServerEndpoints.ShouldBe(["library", "websearch"]);
        built.EnabledFeatures.ShouldBe(["filesystem.read"]);
    }

    [Fact]
    public void TheHouseholdsLanguage_IsEveryAgentsThatPinsNone()
    {
        var nabu = new AgentDefinition { Id = "nabu", Name = "Nabu", Model = "m", McpServerEndpoints = [], Language = "es" };
        var jack = new AgentDefinition { Id = "jack", Name = "Jack", Model = "m", McpServerEndpoints = [] };

        var agents = HouseholdAgents.Of(Household(
            new AgentsSection
            {
                Agents = [new HouseholdAgent { Id = "nabu", Definition = nabu }, new HouseholdAgent { Id = "jack", Definition = jack }]
            },
            settings: new HouseholdSettings { Name = "Casa", TimeZone = "Europe/Paris", Language = "fr" }), _shared);

        agents.Find("nabu")!.Language.ShouldBe("es");
        agents.Find("jack")!.Language.ShouldBe("fr");
        agents.TimeZone.ShouldBe("Europe/Paris");
    }

    [Fact]
    public void AFeatureTurnedOff_WithholdsItsSharedServer_AndMemoryAndWorkers()
    {
        var agents = HouseholdAgents.Of(Household(
            new AgentsSection(),
            features: new FeaturesSection { Scheduling = false, Idealista = false, Memory = false, Workers = false }), _shared);

        var jonas = agents.Find("jonas")!;
        jonas.McpServerEndpoints.ShouldBe(["http://mcp-timers:8080/mcp", "http://mcp-sugary:8080/mcp", "vault", "sandbox", "websearch", "printer"]);
        jonas.EnabledFeatures.ShouldBe(["filesystem"]);
        agents.WithheldServers.ShouldBe(["http://mcp-scheduling:8080/mcp", "http://mcp-idealista:8080/mcp"]);
    }

    // A person's own agent is offered what the household offers its own.
    [Fact]
    public void APersonsOwnAgent_IsOfferedWhatTheHouseholdOffers()
    {
        var agents = HouseholdAgents.Of(Household(new AgentsSection(), features: new FeaturesSection { Idealista = false }), _shared);
        var custom = new AgentDefinition
        {
            Id = "custom-1", Name = "Mine", Model = "m",
            McpServerEndpoints = ["http://MCP-IDEALISTA:8080/mcp", "vault"], EnabledFeatures = ["memory"]
        };

        var offered = agents.Offered(custom);

        offered.McpServerEndpoints.ShouldBe(["vault"]);
        offered.EnabledFeatures.ShouldBe(["memory"]);
    }

    // A template names the compose hosts the deployment runs its shared servers at; one that runs none
    // of them dials none. Its household's own servers are named by name, and each turn decides them.
    [Fact]
    public void ATemplate_ReachesOnlyTheSharedServersTheDeploymentRuns()
    {
        var jonas = HouseholdAgents.Of(Household(new AgentsSection()), new SharedServers { Timers = "http://mcp-timers:8080/mcp" })
            .Find("jonas")!;

        jonas.McpServerEndpoints.ShouldBe(["http://mcp-timers:8080/mcp", "vault", "sandbox", "websearch", "printer"]);
    }

    [Fact]
    public void AnEntryNamingATemplateNotShipped_IsLeftOut_AndSaidSo()
    {
        var agents = HouseholdAgents.Of(Household(new AgentsSection
        {
            Agents =
            [
                new HouseholdAgent { Id = "ana", Template = "jonas", Name = "Ana" },
                new HouseholdAgent { Id = "old", Template = "retired", Name = "Old" }
            ]
        }), _shared);

        agents.Agents.Select(a => a.Id).ShouldBe(["ana"]);
        agents.Problems.ShouldHaveSingleItem().ShouldContain("retired");
    }

    // Which of the own servers an agent names its household never set up, asked at each turn: every
    // one it holds no live credential for, each once, whatever agent names it — a renamed one, a
    // second one from the template — and beside the shared servers its features withhold.
    [Fact]
    public void NotSetUp_IsEveryOwnServerNamedThatTheHouseholdHoldsNoCredentialFor()
    {
        var agents = HouseholdAgents.Of(Household(
            new AgentsSection
            {
                Agents =
                [
                    new HouseholdAgent { Id = "ana", Template = "jonas", Name = "Ana" },
                    new HouseholdAgent { Id = "jonas-2", Template = "jonas", Name = "Jonas Two" },
                    new HouseholdAgent { Id = "sugary", Template = "sugary", Name = "Sugary" }
                ]
            },
            features: new FeaturesSection { Scheduling = false, Idealista = true },
            ownServers: ["vault", "printer"]), _shared);

        var named = agents.Agents.SelectMany(a => a.McpServerEndpoints).ToList();

        agents.NotSetUp(named).ShouldBe(["sandbox", "websearch"]);
        agents.NotSetUp(agents.Find("sugary")!.McpServerEndpoints).ShouldBeEmpty();
        agents.WithheldServers.ShouldBe(["http://mcp-scheduling:8080/mcp"]);
    }

    [Fact]
    public void NotSetUp_ForAViewThatNamesNoOwnServers_IsEveryOneNamed()
    {
        var agents = HouseholdAgents.Of(Household(new AgentsSection(), ownServers: null), _shared);

        agents.NotSetUp(["vault", "http://mcp-timers:8080/mcp", "websearch"]).ShouldBe(["vault", "websearch"]);
    }

    // The home's every server is its own, and agents a stack names in code reach whatever it hosts.
    [Fact]
    public void NotSetUp_ForTheHome_OrAgentsNoHouseholdGave_IsNone()
    {
        var home = HouseholdAgents.Of(Household(new AgentsSection(), ownServers: null) with { IsHome = true }, _shared);

        home.NotSetUp(["vault", "homeassistant"]).ShouldBeEmpty();
        HouseholdAgents.Fixed([]).NotSetUp(["vault"]).ShouldBeEmpty();
    }

    // A household's voice turns go to one agent of its: the one its owner marked, whatever template it
    // was made from — a Jonas answers aloud as well as a Nabu does.
    [Fact]
    public void TheVoiceAgent_IsTheOneTheOwnerMarked()
    {
        var agents = HouseholdAgents.Of(Household(new AgentsSection
        {
            DefaultAgent = "jonas",
            VoiceAgent = "jonas",
            Agents =
            [
                new HouseholdAgent { Id = "jonas", Template = "jonas", Name = "Jonas" },
                new HouseholdAgent { Id = "nabu", Template = "nabu", Name = "Luna" }
            ]
        }), _shared);

        agents.VoiceAgent.ShouldBe("jonas");
    }

    // Marked none, it is the household's agent made from the voice template, the first if it has two;
    // the home's seeded Nabu, which names no template, by its id.
    [Theory]
    [InlineData("nabu-2", "nabu-2")]
    [InlineData(null, "nabu")]
    public void TheVoiceAgent_MarkedNone_IsTheAgentOfTheVoiceTemplate(string? templated, string expected)
    {
        HouseholdAgent[] entries = templated is null
            ?
            [
                new HouseholdAgent { Id = "jonas", Template = "jonas", Name = "Jonas" },
                new HouseholdAgent { Id = "nabu", Definition = new AgentDefinition { Id = "nabu", Name = "Nabu", Model = "m", McpServerEndpoints = [] } }
            ]
            :
            [
                new HouseholdAgent { Id = "jonas", Template = "jonas", Name = "Jonas" },
                new HouseholdAgent { Id = templated, Template = "nabu", Name = "Luna" },
                new HouseholdAgent { Id = "nabu-3", Template = "nabu", Name = "Sol" }
            ];

        HouseholdAgents.Of(Household(new AgentsSection { DefaultAgent = "jonas", Agents = entries }), _shared)
            .VoiceAgent.ShouldBe(expected);
    }

    // A household with none answers aloud by nobody: its voice turns name the voice template's id, which
    // it has no agent by, and are refused with the way to set one up.
    [Fact]
    public void TheVoiceAgent_OfAHouseholdWithNone_IsTheVoiceTemplatesId()
    {
        HouseholdAgents.Of(Household(new AgentsSection()), _shared).VoiceAgent.ShouldBe(AgentTemplates.Nabu);
    }

    [Fact]
    public void AVoiceAgentNamedThatIsNoneOfTheHouseholds_IsRefused()
    {
        HouseholdSections.AgentsSchema.Problems(HouseholdSections.AgentsSchema.Write(new AgentsSection
        {
            DefaultAgent = "jonas",
            VoiceAgent = "ghost",
            Agents = [new HouseholdAgent { Id = "jonas", Template = "jonas", Name = "Jonas" }]
        })).ShouldContain("the voice agent ghost is none of the household's agents");
    }

    private static AgentHouseholdConfig Household(
        AgentsSection agents, HouseholdSettings? settings = null, FeaturesSection? features = null,
        IReadOnlyList<string>? ownServers = null) =>
        new("h1", IsHome: false,
            settings ?? new HouseholdSettings { Name = "Casa", TimeZone = "Europe/Madrid" },
            agents,
            features ?? new FeaturesSection { Idealista = true },
            new SpacesSection(),
            OpenRouterApiKey: null,
            OwnServers: ownServers);
}