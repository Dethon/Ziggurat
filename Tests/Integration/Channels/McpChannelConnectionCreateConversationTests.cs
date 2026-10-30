using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Channels;
using Infrastructure.Conversations;
using McpChannelSignalR.Services;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Integration.McpServers;
using SignalRCreateConversation = McpChannelSignalR.McpTools.CreateConversationTool;
using VoiceCreateConversation = McpChannelVoice.McpTools.CreateConversationTool;

namespace Tests.Integration.Channels;

// Whose a minted topic is crosses the boundary it really crosses: the agent's channel connection
// hands the turn's principal to create_conversation over MCP, and the channel that keeps topics
// decides from it — a person's account gives a topic they own, a turn acting for none gives one
// the household shares. The channel that asked for the review copy never names either (ADR 0046).
public sealed class McpChannelConnectionCreateConversationTests
{
    private static readonly Principal Fran = new("sugary", "u-fran", new Account("t-home", "u-fran"));
    private static readonly Principal Bob = new("telegram", "bob", new Account("t-home", "u-bob"));

    private readonly List<TopicMetadata> _saved = [];
    private readonly IThreadStateStore _store;

    public McpChannelConnectionCreateConversationTests()
    {
        var store = new Mock<IThreadStateStore>();
        store.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>(), It.IsAny<bool>()))
            .Callback((TopicMetadata topic, bool _) => _saved.Add(topic))
            .Returns(Task.CompletedTask);
        _store = store.Object;
    }

    [Fact]
    public async Task AReviewCopyOfAPersonsTurn_IsMintedAsTheirTopic()
    {
        await using var server = await StartWebChatAsync();
        await using var connection = await ConnectAsync(server.Endpoint);

        var id = await connection.CreateConversationAsync(
            "sugary", "Meal: toast", "Fran", "two slices of toast", null, "11:22", Fran, group: false, CancellationToken.None);

        id.ShouldBe("11:22");
        var topic = _saved.ShouldHaveSingleItem();
        (topic.OwnerId, topic.TenantId, topic.Shared, topic.OwnerName).ShouldBe(("u-fran", "t-home", false, "Fran"));
    }

    [Fact]
    public async Task AReviewCopyOfATurnActingForNoAccount_IsMintedAsTheHouseholds()
    {
        await using var server = await StartWebChatAsync();
        await using var connection = await ConnectAsync(server.Endpoint);

        await connection.CreateConversationAsync(
            "sugary", "Meal: toast", "kitchen", "two slices of toast", null, "11:22",
            new Principal("voice", "household"), group: false, CancellationToken.None);

        var topic = _saved.ShouldHaveSingleItem();
        (topic.OwnerId, topic.Shared).ShouldBe(((string?)null, true));
    }

    [Fact]
    public async Task AReviewCopyOfAPrivateTelegramChat_IsMintedAsItsPersonsTopic()
    {
        await using var server = await StartWebChatAsync();
        await using var connection = await ConnectAsync(server.Endpoint);

        await connection.CreateConversationAsync(
            "jack", "Telegram: Fran", "fran", "what's on today", null, "42:42",
            new Principal("telegram", "fran", Fran.Account), group: false, CancellationToken.None);

        var topic = _saved.ShouldHaveSingleItem();
        (topic.OwnerId, topic.TenantId, topic.Shared).ShouldBe(("u-fran", "t-home", false));
    }

    // A group's conversation is everyone's in it, whoever's turn opened it: the copy is the
    // household's, though the turn still acts for its sender.
    [Fact]
    public async Task AReviewCopyOfATelegramGroup_IsMintedAsTheHouseholds_ThoughTheTurnActsForItsSender()
    {
        await using var server = await StartWebChatAsync();
        await using var connection = await ConnectAsync(server.Endpoint);

        await connection.CreateConversationAsync(
            "jack", "Telegram: Family", "bob", "what's on today", null, "-100200:-100200", Bob,
            group: true, CancellationToken.None);

        var topic = _saved.ShouldHaveSingleItem();
        (topic.OwnerId, topic.TenantId, topic.Shared, topic.OwnerName)
            .ShouldBe(((string?)null, "t-home", true, (string?)null));
    }

    // Voice mints a topic for a fire only it delivers, and it is the fire's person's like any other.
    [Fact]
    public async Task AVoiceOnlyDelivery_IsMintedAsItsPersonsTopic()
    {
        await using var server = await StartVoiceAsync();
        await using var connection = await ConnectAsync(server.Endpoint);

        await connection.CreateConversationAsync(
            "nabu", "Scheduled task", "scheduler", "the bins go out tonight", null, null, Fran, group: false, CancellationToken.None);

        _saved.ShouldHaveSingleItem().OwnerId.ShouldBe("u-fran");
    }

    private static async Task<McpChannelConnection> ConnectAsync(string endpoint)
    {
        var connection = new McpChannelConnection("signalr");
        await connection.ConnectAsync(endpoint, CancellationToken.None);
        return connection;
    }

    private Task<RunningServer> StartWebChatAsync()
    {
        var sessions = new SessionService();
        return InMemoryMcpServer.StartAsync(services => services
            .AddSingleton(sessions)
            .AddSingleton(new StreamService(
                sessions, _store, new Mock<IPushNotificationService>().Object, NullLogger<StreamService>.Instance))
            .AddSingleton(new Mock<IHubNotificationSender>().Object)
            .AddSingleton(_store)
            .AddSingleton<IConversationFactory>(new ConversationFactory(_store, TimeProvider.System))
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SignalRCreateConversation>());
    }

    private Task<RunningServer> StartVoiceAsync()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = new ConversationFactory(_store, TimeProvider.System);
        return InMemoryMcpServer.StartAsync(services => services
            .AddSingleton(new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
            {
                ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen" }
            }))
            .AddSingleton(new VoiceDeliveryRegistry(
                time, TimeSpan.FromMinutes(5), new ReplyTextAccumulator(), NullLogger<VoiceDeliveryRegistry>.Instance))
            .AddSingleton(new VoiceConversationManager(
                factory, new ReplyTextAccumulator(), time, TimeSpan.FromMinutes(5),
                NullLogger<VoiceConversationManager>.Instance))
            .AddSingleton(new SatelliteSessionRegistry())
            .AddSingleton<IConversationFactory>(factory)
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<VoiceCreateConversation>());
    }
}