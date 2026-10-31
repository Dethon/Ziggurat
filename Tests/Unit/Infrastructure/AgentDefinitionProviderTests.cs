using Domain.DTOs;
using Domain.DTOs.Channel;
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

    private static readonly Account User1 = new("home", "user1");
    private static readonly Account User2 = new("home", "user2");

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
    public async Task GetById_CustomAgent_ReturnsDefinition()
    {
        var customDef = new AgentDefinition
        {
            Id = "custom-123",
            Name = "Custom",
            Model = "test-model",
            McpServerEndpoints = [],
            EnabledFeatures = []
        };
        await _customAgentRegistry.AddAsync(User1, customDef);

        var result = _sut.GetById("custom-123", User1);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Custom");
        result.EnabledFeatures.ShouldBeEmpty();
    }

    // The same person id in another household is somebody else, and so is another person of the
    // same household: a custom agent's id resolves for neither, nor for a caller naming nobody.
    [Fact]
    public async Task GetById_CustomAgentOfAnotherOwner_ReturnsNull()
    {
        await _customAgentRegistry.AddAsync(User1, new AgentDefinition
        {
            Id = "custom-123",
            Name = "Custom",
            Model = "test-model",
            McpServerEndpoints = []
        });

        _sut.GetById("custom-123", User2).ShouldBeNull();
        _sut.GetById("custom-123", User1 with { TenantId = "elsewhere" }).ShouldBeNull();
        _sut.GetById("custom-123").ShouldBeNull();
    }

    [Fact]
    public void GetById_UnknownAgent_ReturnsNull()
    {
        var result = _sut.GetById("nonexistent");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetById_BuiltInTakesPrecedenceOverCustomWithSameId()
    {
        var customDef = new AgentDefinition
        {
            Id = "built-in",
            Name = "Impostor",
            Model = "test-model",
            McpServerEndpoints = []
        };
        await _customAgentRegistry.AddAsync(User1, customDef);

        var result = _sut.GetById("built-in", User1);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Built-In");
    }

    [Fact]
    public async Task GetById_RemovedCustomAgent_ReturnsNull()
    {
        var customDef = new AgentDefinition
        {
            Id = "custom-456",
            Name = "Temp",
            Model = "test-model",
            McpServerEndpoints = []
        };
        await _customAgentRegistry.AddAsync(User1, customDef);
        await _customAgentRegistry.RemoveAsync(User1, "custom-456");

        var result = _sut.GetById("custom-456", User1);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAll_NoUserId_ReturnsOnlyBuiltInAgents()
    {
        await _customAgentRegistry.AddAsync(User1, new AgentDefinition
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
    public async Task RegisterCustomAgent_ReturnsDefinitionWithCustomPrefixedId()
    {
        var registration = new CustomAgentRegistration
        {
            Name = "MyBot",
            Description = "A custom bot",
            Model = "gpt-4",
            McpServerEndpoints = []
        };

        var result = await _sut.RegisterCustomAgentAsync(User1, registration);

        result.ShouldNotBeNull();
        result.Id.ShouldStartWith("custom-");
        result.Name.ShouldBe("MyBot");
        result.Description.ShouldBe("A custom bot");
        result.Model.ShouldBe("gpt-4");
    }

    [Fact]
    public async Task RegisterCustomAgent_TwoAgentsSameUser_BothStoredWithDifferentIds()
    {
        var reg1 = new CustomAgentRegistration { Name = "Bot1", Model = "m1", McpServerEndpoints = [] };
        var reg2 = new CustomAgentRegistration { Name = "Bot2", Model = "m2", McpServerEndpoints = [] };

        var def1 = await _sut.RegisterCustomAgentAsync(User1, reg1);
        var def2 = await _sut.RegisterCustomAgentAsync(User1, reg2);

        def1.Id.ShouldNotBe(def2.Id);
        var all = _sut.GetAll(User1);
        all.Count.ShouldBe(3); // 1 built-in + 2 custom
        all.Select(a => a.Name).ShouldContain("Bot1");
        all.Select(a => a.Name).ShouldContain("Bot2");
    }

    [Fact]
    public async Task RegisterCustomAgent_DifferentUsers_AgentsIsolated()
    {
        await _sut.RegisterCustomAgentAsync(User1, new CustomAgentRegistration { Name = "UserOneBot", Model = "m1", McpServerEndpoints = [] });
        await _sut.RegisterCustomAgentAsync(User2, new CustomAgentRegistration { Name = "UserTwoBot", Model = "m1", McpServerEndpoints = [] });

        var user1All = _sut.GetAll(User1);
        var user2All = _sut.GetAll(User2);

        user1All.Select(a => a.Name).ShouldContain("UserOneBot");
        user1All.Select(a => a.Name).ShouldNotContain("UserTwoBot");
        user2All.Select(a => a.Name).ShouldContain("UserTwoBot");
        user2All.Select(a => a.Name).ShouldNotContain("UserOneBot");
    }

    [Fact]
    public async Task RegisterCustomAgent_AllFieldsMapped()
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

        var result = await _sut.RegisterCustomAgentAsync(User1, registration);

        result.WhitelistPatterns.ShouldBe(["pattern1"]);
        result.CustomInstructions.ShouldBe("Be helpful");
        result.EnabledFeatures.ShouldBe(["feature1"]);
        result.ProviderRouting!.Sort.ShouldBe(ProviderSort.Throughput);
    }

    [Fact]
    public async Task UnregisterCustomAgent_ExistingAgent_ReturnsTrue()
    {
        var def = await _sut.RegisterCustomAgentAsync(User1, new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });

        var result = await _sut.UnregisterCustomAgentAsync(User1, def.Id);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task UnregisterCustomAgent_NonExistentAgent_ReturnsFalse()
    {
        var result = await _sut.UnregisterCustomAgentAsync(User1, "custom-nonexistent");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task UnregisterCustomAgent_WrongUser_ReturnsFalse()
    {
        var def = await _sut.RegisterCustomAgentAsync(User1, new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });

        var result = await _sut.UnregisterCustomAgentAsync(User2, def.Id);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task UnregisterCustomAgent_AgentRemovedFromList()
    {
        var def = await _sut.RegisterCustomAgentAsync(User1, new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });
        await _sut.UnregisterCustomAgentAsync(User1, def.Id);

        var all = _sut.GetAll(User1);

        all.Count.ShouldBe(1); // only built-in
        all.Select(a => a.Id).ShouldNotContain(def.Id);
    }

    [Fact]
    public async Task GetAll_BuiltInAgentsAlwaysFirst()
    {
        await _sut.RegisterCustomAgentAsync(User1, new CustomAgentRegistration { Name = "Custom1", Model = "m1", McpServerEndpoints = [] });

        var result = _sut.GetAll(User1);

        result.First().Id.ShouldBe("built-in");
    }
}