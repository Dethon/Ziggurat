using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class DomainToolRegistryDottedTests
{
    private readonly Mock<IDomainToolFeature> _filesystemFeature = new();
    private readonly Mock<IDomainToolFeature> _schedulingFeature = new();
    private readonly DomainToolRegistry _registry;

    public DomainToolRegistryDottedTests()
    {
        _filesystemFeature.Setup(f => f.FeatureName).Returns("filesystem");
        _schedulingFeature.Setup(f => f.FeatureName).Returns("scheduling");
        _registry = new DomainToolRegistry([_filesystemFeature.Object, _schedulingFeature.Object]);
    }

    [Fact]
    public void GetToolsForFeatures_BareFeatureName_PassesNullEnabledTools()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        _registry.GetToolsForFeatures(["filesystem"], new FeatureConfig()).ToList();

        captured.ShouldNotBeNull();
        captured.EnabledTools.ShouldBeNull();
    }

    [Fact]
    public void GetToolsForFeatures_DottedNames_PassesToolFilter()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        _registry.GetToolsForFeatures(["filesystem.read", "filesystem.move"], new FeatureConfig()).ToList();

        captured.ShouldNotBeNull();
        captured.EnabledTools.ShouldNotBeNull();
        captured.EnabledTools.Count.ShouldBe(2);
        captured.EnabledTools.ShouldContain("read");
        captured.EnabledTools.ShouldContain("move");
    }

    [Fact]
    public void GetToolsForFeatures_BareAndDottedMixed_BareWins()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        _registry.GetToolsForFeatures(["filesystem", "filesystem.read"], new FeatureConfig()).ToList();

        captured.ShouldNotBeNull();
        captured.EnabledTools.ShouldBeNull();
    }

    [Fact]
    public void GetToolsForFeatures_DottedNames_CaseInsensitive()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        _registry.GetToolsForFeatures(["FileSystem.Read", "FILESYSTEM.Move"], new FeatureConfig()).ToList();

        captured.ShouldNotBeNull();
        captured.EnabledTools.ShouldNotBeNull();
        captured.EnabledTools.ShouldContain("Read");
        captured.EnabledTools.ShouldContain("Move");
    }

    [Fact]
    public void GetToolsForFeatures_UnknownFeature_SkipsGracefully()
    {
        var tools = _registry.GetToolsForFeatures(["nonexistent.read"], new FeatureConfig()).ToList();
        tools.ShouldBeEmpty();
    }

    [Fact]
    public void GetToolsForFeatures_PreservesSubAgentFactory()
    {
        FeatureConfig? captured = null;
        _filesystemFeature
            .Setup(f => f.GetTools(It.IsAny<FeatureConfig>()))
            .Callback<FeatureConfig>(c => captured = c)
            .Returns([]);

        Func<SubAgentDefinition, DisposableAgent> factory = _ => null!;
        var config = new FeatureConfig(SubAgentFactory: factory);

        _registry.GetToolsForFeatures(["filesystem.read"], config).ToList();

        captured.ShouldNotBeNull();
        captured.SubAgentFactory.ShouldBe(factory);
    }

    [Fact]
    public void GetPromptsForFeatures_DottedNames_ResolvesToFeature()
    {
        _filesystemFeature.Setup(f => f.Prompt).Returns("filesystem prompt");

        var prompts = _registry.GetPromptsForFeatures(["filesystem.read"]).ToList();

        prompts.Count.ShouldBe(1);
        prompts[0].Text.ShouldBe("filesystem prompt");
    }

    [Fact]
    public void GetPromptsForFeatures_DuplicateFeatureFromMultipleDots_ReturnsPromptOnce()
    {
        _filesystemFeature.Setup(f => f.Prompt).Returns("filesystem prompt");

        var prompts = _registry.GetPromptsForFeatures(["filesystem.read", "filesystem.move"]).ToList();

        prompts.Count.ShouldBe(1);
    }
}