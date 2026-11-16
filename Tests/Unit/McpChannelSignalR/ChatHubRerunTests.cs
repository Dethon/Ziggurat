using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Mcp.Hosting;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// A reroll's turn is the person's message sent again as itself: the agent is told which message the
// conversation already ends on, so it is answered again rather than said twice.
public sealed class ChatHubRerunTests : IDisposable
{
    private const string TopicId = "topic-1";

    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly ChannelInbox _inbox = new(new FakeTimeProvider());
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly ChatHub _hub;

    public ChatHubRerunTests()
    {
        _streamService = new StreamService(
            _sessionService, new Mock<IThreadStateStore>().Object, new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);
        var catalog = new MutableAgentCatalog();
        catalog.Replace([
            new AgentCatalogEntry("jack", "Jack", null, DefaultModel: "text/only",
                PatchableModels: [new PatchableModel("other/model", "Other", [])], DefaultModelAttachmentKinds: [])
        ]);
        var households = new HouseholdSpacesAccounts();
        households.SavedAgents["home"] = new AgentsSection
        {
            Agents = [new HouseholdAgent { Id = "jack", Definition = new AgentDefinition
            {
                Id = "jack", Name = "Jack", Model = "text/only", McpServerEndpoints = []
            } }]
        };

        _hub = new ChatHub(
            _sessionService, _streamService, approvalService: null!,
            new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast), catalog,
            _store.Object, pushSubscriptionStore: null!, attachmentService: null!,
            new Mock<IHubNotificationSender>().Object, new RetentionSettings(), new DictationSettings(),
            new VouchingAccounts(), households, NullLogger<ChatHub>.Instance)
        {
            Context = new SignedInCaller()
        };

        _sessionService.StartSession(TopicId, "jack", 7, 42, "default", "Chat");
    }

    public void Dispose() => _streamService.Dispose();

    [Fact]
    public async Task SendRerun_TellsTheAgentWhichMessageItAnswersAgain_OnTheModelAskedFor()
    {
        var patch = new AgentConfigPatch { Model = "other/model" };

        var emitted = await EmittedAsync(ct => _hub.SendRerun(TopicId, "hola", null, patch, null, "u1", ct));

        emitted.RerunOf.ShouldBe("u1");
        emitted.Content.ShouldBe("hola");
        emitted.ConfigPatch.ShouldBe(patch);
    }

    [Fact]
    public async Task SendMessage_AnswersNothingAgain()
    {
        var emitted = await EmittedAsync(ct => _hub.SendMessage(TopicId, "hola", null, null, null, ct));

        emitted.RerunOf.ShouldBeNull();
    }

    // Continue asks the model to carry on the reply that was cut off: its ask goes hidden, in the words
    // the agent is told to read as one.
    [Fact]
    public async Task SendContinue_AsksForTheCutOffReplyToGoOn()
    {
        var cut = new ChatMessage(ChatRole.Assistant, "Once upon ");
        cut.MarkInterrupted();
        _store.Setup(s => s.GetMessagesAsync(new AgentKey("7:42", "jack").ToString()))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "a story"), cut]);

        var emitted = await EmittedAsync(ct => _hub.SendContinue(TopicId, null, null, ct));

        emitted.Continuation.ShouldBeTrue();
        emitted.Content.ShouldBe(Continuation.Instruction);
    }

    [Fact]
    public async Task SendContinue_AReplyThatEnded_IsRefused()
    {
        _store.Setup(s => s.GetMessagesAsync(new AgentKey("7:42", "jack").ToString()))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "a story"), new ChatMessage(ChatRole.Assistant, "The end.")]);

        var refused = await Should.ThrowAsync<HubException>(async () =>
        {
            await foreach (var _ in _hub.SendContinue(TopicId, null, null, CancellationToken.None))
            {
            }
        });

        refused.Message.ShouldBe(TopicRefusals.NothingToContinue);
    }

    // The send waits for a reply nobody gives here, so it is read until the agent has been told, then let go.
    private async Task<ChannelMessageNotification> EmittedAsync(
        Func<CancellationToken, IAsyncEnumerable<ChatStreamMessage>> send)
    {
        await _inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in send(cts.Token))
            {
            }
        });

        ChannelMessageNotification? emitted = null;
        await Eventually.Until(async () =>
        {
            emitted ??= (await _inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None))
                .Select(i => i.Message).FirstOrDefault(m => m is not null);
            return emitted is not null;
        }, "the agent is told of the turn");
        await cts.CancelAsync();
        await reading.ContinueWith(_ => { });
        return emitted!;
    }
}