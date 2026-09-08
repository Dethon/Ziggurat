using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Skills;
using Infrastructure.Metrics;
using Infrastructure.Utils;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;
using Tests.Integration.McpServers;
using Tests.Unit.Infrastructure.Helpers;

namespace Tests.Unit.Infrastructure.Agents;

// What the model is handed when its servers ship a skill: instructions ending in the bare list,
// one tool to load with, and nothing else of the framework's — no resource or script tool, no
// approval round trip. Driven through a real server over HTTP and the real approval client, so
// the tool the eval records is the tool the deployment runs.
public class McpAgentSkillsTests
{
    private const string Skill = "test-skill";

    private static Task<RunningServer> StartAsync(params SkillText[] skills) =>
        InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FailingTools>()
            .AddSkills(skills));

    private static McpAgent Agent(IChatClient chatClient, string endpoint) =>
        new(
            TestAgentSpec.Default with { McpServerEndpoints = [McpServerEndpoint.Configured(endpoint)] },
            chatClient,
            new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);

    [Fact]
    public async Task TheInstructions_EndWithTheAdvertisedList_AndTheSkillsSectionExplainsIt()
    {
        await using var server = await StartAsync(new SkillText(Skill, "Does test things.", "# Test\n\nDo the thing."));
        var (chatClient, captured) = Capturing();
        await using var agent = Agent(chatClient, server.Endpoint);

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var instructions = captured.ShouldHaveSingleItem().ShouldNotBeNull().Instructions.ShouldNotBeNull();
        instructions.ShouldContain("## Skills");
        instructions.TrimEnd().ShouldEndWith("</skill>");
        instructions[instructions.LastIndexOf("<skill>", StringComparison.Ordinal)..]
            .ShouldContain($"<name>{Skill}</name>");
        instructions.ShouldNotContain("read_skill_resource");
    }

    [Fact]
    public async Task TheTools_CarryTheLoadTool_AndNeitherTheResourceNorTheScriptTool()
    {
        await using var server = await StartAsync(new SkillText(Skill, "Does test things.", "# Test\n\nDo the thing."));
        var (chatClient, captured) = Capturing();
        await using var agent = Agent(chatClient, server.Endpoint);

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var tools = captured.ShouldHaveSingleItem().ShouldNotBeNull().Tools.ShouldNotBeNull().Select(t => t.Name).ToList();
        tools.ShouldContain(AgentSkillsProvider.LoadSkillToolName);
        tools.ShouldNotContain(AgentSkillsProvider.ReadSkillResourceToolName);
        tools.ShouldNotContain(AgentSkillsProvider.RunSkillScriptToolName);
    }

    // The load tool wears the repo's face: our description, and a schema that names exactly the
    // session's skills, so a garbage name is impossible at the schema level and the model is told
    // when not to call it (a warm-up probe is the failure this guards against).
    [Fact]
    public async Task TheLoadTool_CarriesTheReposDescription_AndAnEnumOfTheSessionsSkillNames()
    {
        await using var server = await StartAsync(
            new SkillText(Skill, "Does test things.", "# Test\n\nDo the thing."),
            new SkillText("other-skill", "Does other things.", "# Other\n\nDo the other thing."));
        var (chatClient, captured) = Capturing();
        await using var agent = Agent(chatClient, server.Endpoint);

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var load = captured.ShouldHaveSingleItem().ShouldNotBeNull().Tools.ShouldNotBeNull()
            .OfType<AIFunction>().Where(t => t.Name == SkillsProvider.LoadToolName).ShouldHaveSingleItem();
        load.Description.ShouldBe(SkillsProvider.LoadToolDescription);
        var skillName = load.JsonSchema.GetProperty("properties").GetProperty("skillName");
        skillName.GetProperty("type").GetString().ShouldBe("string");
        skillName.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldBe([Skill, "other-skill"]);
        load.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(["skillName"]);
    }

    [Fact]
    public async Task AnAgentWhoseServersShipNoSkill_GetsNoListAndNoLoadTool()
    {
        await using var server = await StartAsync();
        var (chatClient, captured) = Capturing();
        await using var agent = Agent(chatClient, server.Endpoint);

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var options = captured.ShouldHaveSingleItem().ShouldNotBeNull();
        options.Instructions.ShouldNotBeNull().ShouldNotContain("## Skills");
        options.Instructions.ShouldNotContain("<skill>");
        options.Tools.ShouldNotBeNull().Select(t => t.Name).ShouldNotContain(AgentSkillsProvider.LoadSkillToolName);
    }

    // The load rides the same pipeline as every other tool: it is observed under its own name,
    // with the body as its result, and it never asks anybody — a handler that would reject
    // everything is not consulted.
    [Fact]
    public async Task ALoad_IsAutoApproved_AndObservedUnderItsOwnName()
    {
        await using var server = await StartAsync(new SkillText(Skill, "Does test things.", "# Test\n\nDo the thing."));
        var fake = new StreamingFakeChatClient(
            ToolApprovalResponseFactory.CreateToolCallResponse(
                AgentSkillsProvider.LoadSkillToolName, "call-1", new Dictionary<string, object?> { ["skillName"] = Skill }),
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done")]) { FinishReason = ChatFinishReason.Stop });
        var handler = new TestApprovalHandler(ToolApprovalResult.Rejected);
        var observer = new RecordingObserver();
        var approving = new ToolApprovalChatClient(fake, handler, "conv-skills", observer: observer);
        await using var agent = Agent(approving, server.Endpoint);

        var response = await agent.RunAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Text.ShouldBe("Done");
        handler.RequestedApprovals.ShouldBeEmpty();
        var load = observer.Invocations.ShouldHaveSingleItem();
        load.ToolName.ShouldBe(AgentSkillsProvider.LoadSkillToolName);
        load.Outcome.ShouldBe(ToolInvocationOutcome.Completed);
        load.Result.ShouldNotBeNull().ShouldContain("Do the thing.");
    }

    private static (IChatClient Client, List<ChatOptions?> Captured) Capturing()
    {
        var captured = new List<ChatOptions?>();
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, options, _) => captured.Add(options))
            .Returns(new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] }
            }.ToAsyncEnumerable());
        return (chatClient.Object, captured);
    }

    // The agent runs every turn as a stream, so the canned answers are streamed back as updates.
    private sealed class StreamingFakeChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_responses.Dequeue());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            _responses.Dequeue().ToChatResponseUpdates().ToAsyncEnumerable();

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class RecordingObserver : IToolInvocationObserver
    {
        public List<ToolInvocation> Invocations { get; } = [];

        public void OnInvoked(ToolInvocation invocation) => Invocations.Add(invocation);

        public void OnTurn(TurnObservation turn)
        {
        }
    }
}