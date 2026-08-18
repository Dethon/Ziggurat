using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Tools;
using Domain.Tools.SubAgents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentRunToolTests
{
    private static readonly SubAgentDefinition _testProfile = new()
    {
        Id = "summarizer",
        Name = "Summarizer",
        Description = "Summarizes content",
        Model = "test-model",
        McpServerEndpoints = []
    };

    private static FeatureConfig CreateConfig(
        Func<SubAgentDefinition, DisposableAgent>? factory = null,
        string? userId = null,
        Func<ConversationContext?>? conversationContextProvider = null) =>
        new(SubAgentFactory: factory, UserId: userId,
            ConversationContextProvider: conversationContextProvider);

    private static SubAgentRunTool CreateTool(FeatureConfig config, params SubAgentDefinition[] profiles) =>
        new(new SubAgentRegistryOptions { SubAgents = profiles }, config);

    [Fact]
    public async Task RunAsync_UnknownProfile_ReturnsError()
    {
        var tool = CreateTool(CreateConfig());

        var result = await tool.RunAsync("unknown", "do something");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        result["message"]!.GetValue<string>().ShouldContain("unknown");
    }

    [Fact]
    // A run with no way to spawn is not a dependency that is down: it will refuse identically
    // forever, so the envelope must not invite a retry, and it has to say what to do instead.
    public async Task RunAsync_NullSubAgentFactory_RefusesWithoutInvitingARetry()
    {
        var config = CreateConfig(factory: null);
        var tool = CreateTool(config, _testProfile);

        var result = await tool.RunAsync("summarizer", "do something");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
        result["hint"]!.GetValue<string>().ShouldContain("Do the work in this turn");
    }

    [Fact]
    public async Task RunAsync_ValidProfile_CallsFactoryAndReturnsResult()
    {
        var stubAgent = new StubDisposableAgent("Summary result");
        var factoryCalled = false;
        var config = CreateConfig(factory: def =>
        {
            def.ShouldBe(_testProfile);
            factoryCalled = true;
            return stubAgent;
        });

        var tool = CreateTool(config, _testProfile);

        var result = await tool.RunAsync("summarizer", "summarize this");

        result["status"]!.GetValue<string>().ShouldBe("completed");
        result["result"]!.GetValue<string>().ShouldBe("Summary result");
        factoryCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_FactoryThrows_ReturnsError()
    {
        var config = CreateConfig(factory: _ =>
            throw new InvalidOperationException("factory error"));

        var tool = CreateTool(config, _testProfile);

        var result = await tool.RunAsync("summarizer", "do something");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("internal_error");
        result["message"]!.GetValue<string>().ShouldContain("factory error");
    }

    [Fact]
    public async Task RunAsync_PropagatesUserIdAsSenderOnUserMessage()
    {
        var stubAgent = new StubDisposableAgent("Summary result");
        var config = CreateConfig(factory: _ => stubAgent, userId: "user-42");

        var tool = CreateTool(config, _testProfile);

        var result = await tool.RunAsync("summarizer", "summarize this");

        result["status"]!.GetValue<string>().ShouldBe("completed");
        stubAgent.LastMessages.ShouldNotBeNull();
        var userMessage = stubAgent.LastMessages.Single(m => m.Role == ChatRole.User);
        userMessage.GetSenderId().ShouldBe("user-42");
    }

    [Fact]
    public async Task RunAsync_StampsParentConversationContextVerbatimOnUserMessage()
    {
        // Verbatim: the PARENT's agent id, not "summarizer". Downstream MCP servers scope
        // per-conversation state by {AgentId}:{ConversationId}, so a file_search issued by the
        // parent and a file_download issued by the subagent have to land in the same scope.
        var parentContext = new ConversationContext(
            "jack", "conv-7", "fran", new ReplyTarget("signalr", "conv-7"));
        var stubAgent = new StubDisposableAgent("Summary result");
        var config = CreateConfig(
            factory: _ => stubAgent, userId: "fran", conversationContextProvider: () => parentContext);

        var tool = CreateTool(config, _testProfile);

        var result = await tool.RunAsync("summarizer", "summarize this");

        result["status"]!.GetValue<string>().ShouldBe("completed");
        stubAgent.LastMessages.ShouldNotBeNull();
        var userMessage = stubAgent.LastMessages.Single(m => m.Role == ChatRole.User);
        userMessage.GetConversationContext().ShouldBe(parentContext);
    }

    [Fact]
    public async Task RunAsync_ResolvesConversationContextPerCall()
    {
        // The provider is invoked at run time, not captured at construction: a FeatureConfig (and
        // the tool built from it) lives as long as the agent, which outlives a single turn.
        var contexts = new Queue<ConversationContext>(
        [
            new ConversationContext("jack", "conv-1", "fran", new ReplyTarget("signalr", "conv-1")),
            new ConversationContext("jack", "conv-2", "fran", new ReplyTarget("signalr", "conv-2"))
        ]);
        var stubAgent = new StubDisposableAgent("result");
        var config = CreateConfig(factory: _ => stubAgent, conversationContextProvider: contexts.Dequeue);

        var tool = CreateTool(config, _testProfile);

        await tool.RunAsync("summarizer", "first");
        stubAgent.LastMessages!.Single(m => m.Role == ChatRole.User)
            .GetConversationContext()!.ConversationId.ShouldBe("conv-1");

        await tool.RunAsync("summarizer", "second");
        stubAgent.LastMessages!.Single(m => m.Role == ChatRole.User)
            .GetConversationContext()!.ConversationId.ShouldBe("conv-2");
    }

    [Fact]
    public async Task RunAsync_ProfileLookup_IsCaseInsensitive()
    {
        var stubAgent = new StubDisposableAgent("result");
        var config = CreateConfig(factory: _ => stubAgent);

        var tool = CreateTool(config, _testProfile);

        var result = await tool.RunAsync("SUMMARIZER", "test");

        result["status"]!.GetValue<string>().ShouldBe("completed");
    }
}

file sealed class StubAgentSession : AgentSession;

file sealed class StubDisposableAgent(string responseText) : DisposableAgent
{
    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, responseText)));
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new StubAgentSession());

    protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
        AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(System.Text.Json.JsonDocument.Parse("{}").RootElement);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new StubAgentSession());
}