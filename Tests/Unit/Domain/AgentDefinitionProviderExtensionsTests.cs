using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class AgentDefinitionProviderExtensionsTests
{
    private readonly Mock<IAgentDefinitionProvider> _provider = new();

    private void Define(string agentId, params string[] enabledFeatures) =>
        _provider.Setup(p => p.GetById(agentId)).Returns(new AgentDefinition
        {
            Id = agentId,
            Name = agentId,
            Model = "test",
            McpServerEndpoints = [],
            EnabledFeatures = enabledFeatures
        });

    [Fact]
    public void HasFeatureEnabled_WithNoAgentId_IsEnabled()
    {
        _provider.Object.HasFeatureEnabled(null, "memory").ShouldBeTrue();
    }

    [Fact]
    public void HasFeatureEnabled_WithAnUnknownAgentId_IsEnabled()
    {
        _provider.Object.HasFeatureEnabled("never-configured", "memory").ShouldBeTrue();
    }

    [Fact]
    public void HasFeatureEnabled_WithAKnownAgentWithoutTheFeature_IsDisabled()
    {
        Define("agent-no-memory", "voice");

        _provider.Object.HasFeatureEnabled("agent-no-memory", "memory").ShouldBeFalse();
    }

    [Fact]
    public void HasFeatureEnabled_ComparesTheFeatureNameCaseInsensitively()
    {
        Define("agent-with-memory", "Memory");

        _provider.Object.HasFeatureEnabled("agent-with-memory", "memory").ShouldBeTrue();
    }
}