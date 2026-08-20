using System.Reflection;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Prompts;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public sealed class SubAgentConversationContextWiringTests : IDisposable
{
    private static readonly PropertyInfo _currentContext =
        typeof(FunctionInvokingChatClient)
            .GetProperty("CurrentContext", BindingFlags.Public | BindingFlags.Static)!;

    private static readonly ConversationContext _parentContext = new(
        "jack", "conv-11", "fran", new ReplyTarget("signalr", "conv-11"));

    private static void SetCurrentContext(ConversationContext? context)
        => _currentContext.SetValue(null, context is null
            ? null
            : new FunctionInvocationContext
            {
                Options = new ChatOptions
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [ConversationContextMeta.OptionsKey] = context
                    }
                }
            });

    public void Dispose() => SetCurrentContext(null);

    [Fact]
    public void Current_ReadsTheContextOfTheEnclosingToolInvocation()
    {
        ConversationContextMeta.Current.ShouldBeNull();

        SetCurrentContext(_parentContext);

        ConversationContextMeta.Current.ShouldBe(_parentContext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeatureConfig_CarriesAProviderResolvingToTheEnclosingToolInvocation(bool subAgent)
    {
        var captured = CaptureFeatureConfig(subAgent);

        captured.ConversationContextProvider.ShouldNotBeNull();

        SetCurrentContext(_parentContext);
        captured.ConversationContextProvider().ShouldBe(_parentContext);
    }

    private static FeatureConfig CaptureFeatureConfig(bool subAgent)
    {
        var captured = new List<FeatureConfig>();
        var registry = new Mock<IDomainToolRegistry>();
        registry
            .Setup(r => r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()))
            .Callback<IEnumerable<string>, FeatureConfig>((_, config) => captured.Add(config))
            .Returns(Enumerable.Empty<AIFunction>());
        registry
            .Setup(r => r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()))
            .Returns(Enumerable.Empty<PromptSection>());

        var definition = new AgentDefinition
        {
            Id = "jack",
            Name = "Jack",
            Model = "test-model",
            McpServerEndpoints = []
        };
        var optionsMonitor = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(new AgentRegistryOptions { Agents = [definition] });

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IThreadStateStore)))
            .Returns(new Mock<IThreadStateStore>().Object);

        var factory = new MultiAgentFactory(
            serviceProvider.Object,
            new AgentDefinitionProvider(optionsMonitor.Object, new CustomAgentRegistry()),
            new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" },
            registry.Object);

        var approvalHandler = new Mock<IToolApprovalHandler>().Object;
        if (subAgent)
        {
            factory.CreateSubAgent(
                new SubAgentDefinition
                {
                    Id = "worker",
                    Name = "Worker",
                    Model = "test-model",
                    McpServerEndpoints = []
                },
                approvalHandler,
                new SpawnContext("conv-1", "fran", [], UsesOutposts: false));
        }
        else
        {
            factory.Create(new AgentKey("conv-11", "jack"), "fran", "jack", approvalHandler);
        }

        return captured.ShouldHaveSingleItem();
    }
}