using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Domain.Security;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Modules;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// A household's spaces are its configuration's (.claude/rules/household-config.md, the spaces view),
// asked of the accounts service with an assertion of the connection's person on the chat hub's
// channel: the household is the one that assertion names, never a word of the browser's. A
// connection is told its household's spaces and joins only one of them. Asked over a real
// connection to the hub as the server registers it, since the refusal is the hub's to make.
public sealed class ChatHubSpacesTests : IAsyncLifetime
{
    private const string Fran = "0123456789abcdef0123456789abcdef";
    private const string Home = "fedcba9876543210fedcba9876543210";
    private const string Away = "abababababababababababababababab";
    private const string Phone = "5e55105e55105e55105e55105e55105e";

    private readonly HouseholdSpacesAccounts _households = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly List<HubConnection> _connections = [];
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        var attachmentSettings = new AttachmentSettings { StoragePath = _root };
        var time = new FakeTimeProvider();
        var sessions = new SessionService();
        var streams = new StreamService(
            sessions, new Mock<IThreadStateStore>().Object, new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddChatSignalR()
            .AddSingleton(sessions)
            .AddSingleton(streams)
            .AddSingleton(new ApprovalService(
                streams, sessions, new Mock<IThreadStateStore>().Object, new Mock<IHubNotificationSender>().Object,
                new Mock<IPushNotificationService>().Object, new AccountConnections(), NullLogger<ApprovalService>.Instance))
            .AddSingleton(new ChannelNotificationEmitter(new ChannelInbox(time), DeliveryPolicy.Broadcast))
            .AddSingleton<ISessionAccounts>(new VouchingAccounts(Home))
            .AddSingleton<IHouseholdConfiguration>(_households)
            .AddSingleton(new Mock<IAgentCatalog>().Object)
            .AddSingleton(new Mock<IThreadStateStore>().Object)
            .AddSingleton(new Mock<IPushSubscriptionStore>().Object)
            .AddSingleton(new AttachmentService(
                attachmentSettings,
                new AttachmentTickets(attachmentSettings, time),
                new AttachmentStore(attachmentSettings, new RetentionSettings(), new FakeConversationLiveness(), time, NullLogger<AttachmentStore>.Instance),
                NullLogger<AttachmentService>.Instance))
            .AddSingleton(new Mock<IHubNotificationSender>().Object)
            .AddSingleton(new RetentionSettings())
            .AddSingleton(new DictationSettings())
            .AddSingleton(TestAssertions.Verifier());

        _app = builder.Build();
        _app.MapHub<ChatHub>("/hubs/chat");
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await _app.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static HouseholdSpace Space(string slug, int order, string? defaultAgent = null) => new()
    {
        Slug = slug, Name = $"The {slug}", AccentColor = "#123456", Order = order, DefaultAgent = defaultAgent
    };

    [Fact]
    public async Task AConnection_IsToldItsHouseholdsSpacesInTheirOrder()
    {
        _households.Saved[Home] = new SpacesSection { Spaces = [Space("kitchen", 2, "jonas"), Space("default", 1)] };
        _households.Saved[Away] = new SpacesSection { Spaces = [Space("attic", 1)] };
        var connection = await ConnectAsync();

        var spaces = await connection.InvokeAsync<IReadOnlyList<HouseholdSpace>>("GetSpaces");

        spaces.ShouldBe([Space("default", 1), Space("kitchen", 2, "jonas")]);
    }

    [Fact]
    public async Task ASpaceOfTheConnectionsHousehold_IsJoined()
    {
        _households.Saved[Home] = new SpacesSection { Spaces = [Space("default", 0), Space("kitchen", 1)] };
        var connection = await ConnectAsync();

        await connection.InvokeAsync("JoinSpace", "kitchen");

        (await connection.InvokeAsync<DictationTicket>("CreateDictationTicket")).ShouldNotBeNull();
    }

    // Another household's space is no space of this one: refused in the words for a space that does
    // not exist, so naming it tells the namer nothing of it.
    [Fact]
    public async Task ASpaceOfAnotherHousehold_IsRefused()
    {
        _households.Saved[Home] = new SpacesSection { Spaces = [Space("default", 0)] };
        _households.Saved[Away] = new SpacesSection { Spaces = [Space("default", 0), Space("attic", 1)] };
        var connection = await ConnectAsync();

        var refused = await Should.ThrowAsync<HubException>(() => connection.InvokeAsync("JoinSpace", "attic"));

        refused.Message.ShouldContain("No such space");
    }

    // A household that never saved its spaces reads them as their default (household-config.md),
    // so a new household has the one default space and nothing else.
    [Fact]
    public async Task AHouseholdThatSavedNoSpaces_HasTheOneDefaultSpace()
    {
        var connection = await ConnectAsync();

        var spaces = await connection.InvokeAsync<IReadOnlyList<HouseholdSpace>>("GetSpaces");
        await connection.InvokeAsync("JoinSpace", SpaceConfig.DefaultSlug);

        spaces.ShouldBe([HouseholdSpace.Default]);
        await Should.ThrowAsync<HubException>(() => connection.InvokeAsync("JoinSpace", "x"));
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var server = _app.GetTestServer();
        var assertion = TestAssertions.Of(new Account(Home, Fran), ChannelIds.SignalR, ["owner"], sessionId: Phone);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[ForwardedAccount.AssertionHeader] = assertion;
            })
            .Build();
        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }
}