using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class AgentDefinitionProviderTests
{
    private static readonly AgentDefinition _builtInAgent = new()
    {
        Id = "built-in",
        Name = "Built-In",
        Model = "test-model",
        McpServerEndpoints = [],
        EnabledFeatures = ["memory"]
    };

    private readonly CustomAgentRegistry _customAgentRegistry = new();
    private readonly AgentDefinitionProvider _sut;

    public AgentDefinitionProviderTests()
    {
        var registryOptions = new AgentRegistryOptions { Agents = [_builtInAgent] };
        var optionsMonitor = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(registryOptions);

        _sut = new AgentDefinitionProvider(optionsMonitor.Object, _customAgentRegistry);
    }

    [Fact]
    public void GetById_CustomAgent_ReturnsDefinition()
    {
        var customDef = new AgentDefinition
        {
            Id = "custom-123",
            Name = "Custom",
            Model = "test-model",
            McpServerEndpoints = [],
            EnabledFeatures = []
        };
        _customAgentRegistry.Add("user1", customDef);

        var result = _sut.GetById("custom-123");

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Custom");
        result.EnabledFeatures.ShouldBeEmpty();
    }

    [Fact]
    public void GetById_UnknownAgent_ReturnsNull()
    {
        var result = _sut.GetById("nonexistent");

        result.ShouldBeNull();
    }

    [Fact]
    public void GetById_BuiltInTakesPrecedenceOverCustomWithSameId()
    {
        var customDef = new AgentDefinition
        {
            Id = "built-in",
            Name = "Impostor",
            Model = "test-model",
            McpServerEndpoints = []
        };
        _customAgentRegistry.Add("user1", customDef);

        var result = _sut.GetById("built-in");

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Built-In");
    }

    [Fact]
    public void GetById_RemovedCustomAgent_ReturnsNull()
    {
        var customDef = new AgentDefinition
        {
            Id = "custom-456",
            Name = "Temp",
            Model = "test-model",
            McpServerEndpoints = []
        };
        _customAgentRegistry.Add("user1", customDef);
        _customAgentRegistry.Remove("user1", "custom-456");

        var result = _sut.GetById("custom-456");

        result.ShouldBeNull();
    }

    [Fact]
    public void GetAll_NoUserId_ReturnsOnlyBuiltInAgents()
    {
        _customAgentRegistry.Add("user1", new AgentDefinition
        {
            Id = "custom-789",
            Name = "Custom",
            Model = "test-model",
            McpServerEndpoints = []
        });

        var result = _sut.GetAll();

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("built-in");
    }

    [Fact]
    public void RegisterCustomAgent_ReturnsDefinitionWithCustomPrefixedId()
    {
        var registration = new CustomAgentRegistration
        {
            Name = "MyBot",
            Description = "A custom bot",
            Model = "gpt-4",
            McpServerEndpoints = []
        };

        var result = _sut.RegisterCustomAgent("user1", registration);

        result.ShouldNotBeNull();
        result.Id.ShouldStartWith("custom-");
        result.Name.ShouldBe("MyBot");
        result.Description.ShouldBe("A custom bot");
        result.Model.ShouldBe("gpt-4");
    }

    [Fact]
    public void RegisterCustomAgent_TwoAgentsSameUser_BothStoredWithDifferentIds()
    {
        var reg1 = new CustomAgentRegistration { Name = "Bot1", Model = "m1", McpServerEndpoints = [] };
        var reg2 = new CustomAgentRegistration { Name = "Bot2", Model = "m2", McpServerEndpoints = [] };

        var def1 = _sut.RegisterCustomAgent("user1", reg1);
        var def2 = _sut.RegisterCustomAgent("user1", reg2);

        def1.Id.ShouldNotBe(def2.Id);
        var all = _sut.GetAll("user1");
        all.Count.ShouldBe(3); // 1 built-in + 2 custom
        all.Select(a => a.Name).ShouldContain("Bot1");
        all.Select(a => a.Name).ShouldContain("Bot2");
    }

    [Fact]
    public void RegisterCustomAgent_DifferentUsers_AgentsIsolated()
    {
        _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "UserOneBot", Model = "m1", McpServerEndpoints = [] });
        _sut.RegisterCustomAgent("user2", new CustomAgentRegistration { Name = "UserTwoBot", Model = "m1", McpServerEndpoints = [] });

        var user1All = _sut.GetAll("user1");
        var user2All = _sut.GetAll("user2");

        user1All.Select(a => a.Name).ShouldContain("UserOneBot");
        user1All.Select(a => a.Name).ShouldNotContain("UserTwoBot");
        user2All.Select(a => a.Name).ShouldContain("UserTwoBot");
        user2All.Select(a => a.Name).ShouldNotContain("UserOneBot");
    }

    [Fact]
    public void RegisterCustomAgent_AllFieldsMapped()
    {
        var registration = new CustomAgentRegistration
        {
            Name = "FullBot",
            Description = "Full description",
            Model = "test-model",
            McpServerEndpoints = [],
            WhitelistPatterns = ["pattern1"],
            CustomInstructions = "Be helpful",
            EnabledFeatures = ["feature1"],
            ProviderRouting = new ProviderRouting { Sort = ProviderSort.Throughput }
        };

        var result = _sut.RegisterCustomAgent("user1", registration);

        result.WhitelistPatterns.ShouldBe(["pattern1"]);
        result.CustomInstructions.ShouldBe("Be helpful");
        result.EnabledFeatures.ShouldBe(["feature1"]);
        result.ProviderRouting!.Sort.ShouldBe(ProviderSort.Throughput);
    }

    // A registered agent's only other routing lever is a `:nitro`/`:floor` model suffix, the dual
    // idiom the built-in agents migrated off. Dropping this on the floor would leave an external
    // host's declared routing silently unapplied, with the request still succeeding.
    [Fact]
    public void RegisterCustomAgent_NoProviderRouting_LeavesItNullForBalancedRouting()
    {
        var result = _sut.RegisterCustomAgent(
            "user1", new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });

        result.ProviderRouting.ShouldBeNull();
    }

    [Fact]
    public void UnregisterCustomAgent_ExistingAgent_ReturnsTrue()
    {
        var def = _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });

        var result = _sut.UnregisterCustomAgent("user1", def.Id);

        result.ShouldBeTrue();
    }

    [Fact]
    public void UnregisterCustomAgent_NonExistentAgent_ReturnsFalse()
    {
        var result = _sut.UnregisterCustomAgent("user1", "custom-nonexistent");

        result.ShouldBeFalse();
    }

    [Fact]
    public void UnregisterCustomAgent_WrongUser_ReturnsFalse()
    {
        var def = _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });

        var result = _sut.UnregisterCustomAgent("user2", def.Id);

        result.ShouldBeFalse();
    }

    [Fact]
    public void UnregisterCustomAgent_AgentRemovedFromList()
    {
        var def = _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });
        _sut.UnregisterCustomAgent("user1", def.Id);

        var all = _sut.GetAll("user1");

        all.Count.ShouldBe(1); // only built-in
        all.Select(a => a.Id).ShouldNotContain(def.Id);
    }

    [Fact]
    public void GetAll_BuiltInAgentsAlwaysFirst()
    {
        _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Custom1", Model = "m1", McpServerEndpoints = [] });

        var result = _sut.GetAll("user1");

        result.First().Id.ShouldBe("built-in");
    }
}