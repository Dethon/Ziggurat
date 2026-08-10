using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class DomainToolRegistryTests
{
    [Fact]
    public void GetPromptsForFeatures_EnabledFeatureWithPrompt_ReturnsPrompt()
    {
        var feature = new Mock<IDomainToolFeature>();
        feature.Setup(f => f.FeatureName).Returns("subagents");
        feature.Setup(f => f.Prompt).Returns("Use subagents proactively.");
        feature.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([feature.Object]);

        var prompts = registry.GetPromptsForFeatures(["subagents"]).ToList();

        prompts.ShouldBe(["Use subagents proactively."]);
    }

    [Fact]
    public void GetPromptsForFeatures_FeatureWithNullPrompt_ReturnsEmpty()
    {
        var feature = new Mock<IDomainToolFeature>();
        feature.Setup(f => f.FeatureName).Returns("scheduling");
        feature.Setup(f => f.Prompt).Returns((string?)null);
        feature.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([feature.Object]);

        var prompts = registry.GetPromptsForFeatures(["scheduling"]).ToList();

        prompts.ShouldBeEmpty();
    }

    [Fact]
    public void GetPromptsForFeatures_DisabledFeature_ReturnsEmpty()
    {
        var feature = new Mock<IDomainToolFeature>();
        feature.Setup(f => f.FeatureName).Returns("subagents");
        feature.Setup(f => f.Prompt).Returns("Use subagents proactively.");
        feature.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([feature.Object]);

        var prompts = registry.GetPromptsForFeatures(["scheduling"]).ToList();

        prompts.ShouldBeEmpty();
    }
}