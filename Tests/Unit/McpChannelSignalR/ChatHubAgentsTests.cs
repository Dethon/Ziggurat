using System.Text.Json;
using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Domain.Prompts;
using Domain.Security;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Modules;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// The chat client's picker is the connection's household's agents (.claude/rules/household-config.md,
// the agent-names view), asked of the accounts service with the session refresh's assertion of the
// connection's person as the spaces are: each agent under the name its household gave it, on the
// model it runs, with the deployment's patchable models and what each model accepts taken from the
// catalogue the agent registered. The home's people see that catalogue as it was registered.
public sealed class ChatHubAgentsTests : IAsyncLifetime
{
    private const string Fran = "0123456789abcdef0123456789abcdef";
    private const string Home = "fedcba9876543210fedcba9876543210";
    private const string Away = "abababababababababababababababab";
    private const string Phone = "5e55105e55105e55105e55105e55105e";
    private const string Luna = "openai/gpt-6-luna";

    private static readonly IReadOnlyList<PatchableModel> _patchable =
    [
        new(Luna, "Luna", [AttachmentKind.Image, AttachmentKind.Document]),
        new(AgentTemplates.Named(AgentTemplates.Jonas)!.Model, "Flash", [AttachmentKind.Image])
    ];

    private static readonly AgentDefinition _jack = new()
    {
        Id = "jack", Name = "Jack", Description = "Downloads.", Model = Luna, ReasoningEffort = "low", McpServerEndpoints = []
    };

    // The home's catalogue, as the agent registers it: its own agents, stamped with the deployment's
    // patchable models and what each agent's model accepts.
    private static readonly IReadOnlyList<AgentCatalogEntry> _registered =
    [
        new("jack", "Jack", "Downloads.", Luna, "low", _patchable, AgentConfigPatch.SupportedEfforts, [AttachmentKind.Image])
    ];

    private readonly HouseholdSpacesAccounts _households = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly List<HubConnection> _connections = [];
    private readonly List<WebApplication> _apps = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        foreach (var app in _apps)
        {
            await app.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AConnectionOfAnotherHousehold_IsShownItsOwnAgentsUnderItsNames()
    {
        _households.SavedAgents[Home] = new AgentsSection { Agents = [new HouseholdAgent { Id = "jack", Definition = _jack }] };
        _households.SavedAgents[Away] = new AgentsSection
        {
            Agents = [new HouseholdAgent { Id = "chef", Template = AgentTemplates.Jonas, Name = "Chef" }]
        };
        var jonas = AgentTemplates.Named(AgentTemplates.Jonas)!;
        var connection = await ConnectAsync(Away);

        var agents = await connection.InvokeAsync<IReadOnlyList<AgentCatalogEntry>>("GetAgents");

        Wire(agents).ShouldBe(Wire([
            new AgentCatalogEntry("chef", "Chef", jonas.Description, jonas.Model, jonas.ReasoningEffort,
                _patchable, AgentConfigPatch.SupportedEfforts, [AttachmentKind.Image], IsHouseholdDefault: true)
        ]));
    }

    [Fact]
    public async Task AConnectionOfTheHome_IsShownTheCatalogueTheAgentRegistered()
    {
        _households.SavedAgents[Home] = new AgentsSection { Agents = [new HouseholdAgent { Id = "jack", Definition = _jack }] };
        var connection = await ConnectAsync(Home);

        var agents = await connection.InvokeAsync<IReadOnlyList<AgentCatalogEntry>>("GetAgents");

        // The household's own one agent is the one it answers by default; the registered catalogue
        // marks none.
        Wire(agents).ShouldBe(Wire(_registered.Select(e => e with { IsHouseholdDefault = true }).ToList()));
    }

    [Fact]
    public async Task AnAgentOfAnotherHousehold_IsNoAgentOfTheConnections()
    {
        _households.SavedAgents[Home] = new AgentsSection { Agents = [new HouseholdAgent { Id = "jack", Definition = _jack }] };
        var connection = await ConnectAsync(Away);

        var jack = await connection.InvokeAsync<bool>("ValidateAgent", "jack");
        var jonas = await connection.InvokeAsync<bool>("ValidateAgent", AgentTemplates.Jonas);

        jack.ShouldBeFalse();
        jonas.ShouldBeTrue();
    }

    // What a file sent to an agent is checked against is that agent as the connection's household
    // runs it: an agent the home's registered catalogue lacks is no unknown agent that may be sent
    // anything, but the household's, on its model, which the deployment says what it reads.
    [Fact]
    public async Task AFileSentToAHouseholdsOwnAgent_IsCheckedAgainstWhatThatAgentsModelReads()
    {
        _households.SavedAgents[Away] = new AgentsSection
        {
            Agents = [new HouseholdAgent { Id = "chef", Template = AgentTemplates.Jonas, Name = "Chef" }]
        };
        var connection = await ConnectAsync(Away);
        var topicId = ConversationIdGenerator.NewTopicId();
        var identity = ConversationIdGenerator.CreateFor(topicId);
        (await connection.InvokeAsync<bool>("StartSession", "chef", topicId, identity.ChatId, identity.ThreadId, "Scan"))
            .ShouldBeTrue();
        AttachmentReference scan = new() { Id = "7-42/scan", FileName = "scan.pdf", MediaType = "application/pdf", SizeBytes = 4 };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var answered = new List<ChatStreamMessage>();
        try
        {
            await foreach (var chunk in connection.StreamAsync<ChatStreamMessage>(
                               "SendMessage", topicId, "read this", null, null, new[] { scan }, cts.Token))
            {
                answered.Add(chunk);
            }
        }
        catch (OperationCanceledException)
        {
        }

        answered.ShouldContain(chunk => (chunk.Content ?? chunk.Error ?? "").Contains("cannot read"));
    }

    // An entry as the browser reads it: its lists come back as other collections than they were sent as.
    private static string Wire(IReadOnlyList<AgentCatalogEntry> agents) => JsonSerializer.Serialize(agents, JsonSerializerOptions.Web);

    private async Task<HubConnection> ConnectAsync(string household)
    {
        var app = await StartAsync(household);
        var server = app.GetTestServer();
        var assertion = TestAssertions.Of(new Account(household, Fran), ChannelIds.SignalR, ["owner"], sessionId: Phone);
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

    private async Task<WebApplication> StartAsync(string household)
    {
        var attachmentSettings = new AttachmentSettings { StoragePath = _root };
        var time = new FakeTimeProvider();
        var sessions = new SessionService();
        var streams = new StreamService(
            sessions, new Mock<IThreadStateStore>().Object, new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);
        var catalog = new MutableAgentCatalog();
        catalog.Replace(_registered);

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
            .AddSingleton<ISessionAccounts>(new VouchingAccounts(household))
            .AddSingleton<IHouseholdConfiguration>(_households)
            .AddSingleton<IAgentCatalog>(catalog)
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

        var app = builder.Build();
        app.MapHub<ChatHub>("/hubs/chat");
        await app.StartAsync();
        _apps.Add(app);
        return app;
    }
}