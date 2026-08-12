using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// The live connection is what hands out permission to touch the upload store, in both
// directions: a ticket scoped to the topic being composed in, and a short-lived URL for a file
// already sent. One upload store serves every space, so the way out is checked against the
// requester's.
public sealed class ChatHubAttachmentTests : IDisposable
{
    private const string TopicId = "topic-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly ChannelInbox _inbox;
    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly AttachmentSettings _settings;
    private readonly AttachmentService _attachments;
    private readonly AttachmentStore _store;
    private readonly ChatHub _hub;

    public ChatHubAttachmentTests()
    {
        _settings = new AttachmentSettings
        {
            StoragePath = _root,
            MaxBytesPerFile = 4096,
            MaxFilesPerMessage = 3,
            TicketTtlSeconds = 60
        };

        _inbox = new ChannelInbox(_time);
        _store = new AttachmentStore(_settings, new RetentionSettings(), _time, NullLogger<AttachmentStore>.Instance);
        _attachments = new AttachmentService(
            _settings,
            new AttachmentTickets(_settings, _time),
            _store,
            NullLogger<AttachmentService>.Instance);

        _streamService = new StreamService(
            _sessionService,
            new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);

        _hub = new ChatHub(
            _sessionService,
            _streamService,
            approvalService: null!,
            new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object,
            threadStore: null!,
            pushSubscriptionStore: null!,
            _attachments,
            new Mock<IHubNotificationSender>().Object,
            new RetentionSettings(),
            new DictationSettings(),
            NullLogger<ChatHub>.Instance)
        {
            Context = new RegisteredCaller()
        };

        _sessionService.StartSession(TopicId, "jack", 7, 42, "default", "Chat");
    }

    public void Dispose()
    {
        _streamService.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CreateUploadTicket_IsScopedToTheTopicAndExpires()
    {
        var ticket = _hub.CreateUploadTicket(TopicId);

        ticket.Token.ShouldNotBeNullOrWhiteSpace();
        ticket.ExpiresAt.ShouldBe(_time.GetUtcNow().AddSeconds(_settings.TicketTtlSeconds));
        _attachments.ResolveUploadScope(ticket.Token, TopicId).ShouldNotBeNull();
        _attachments.ResolveUploadScope(ticket.Token, "another-topic").ShouldBeNull();
    }

    [Fact]
    public void CreateUploadTicket_ForAnUnknownTopic_IsRefused()
    {
        Should.Throw<HubException>(() => _hub.CreateUploadTicket("no-such-topic"));
    }

    [Fact]
    public void GetAttachmentLimits_AnswersWhatTheOperatorConfigured()
    {
        var limits = _hub.GetAttachmentLimits();

        limits.MaxBytesPerFile.ShouldBe(_settings.MaxBytesPerFile);
        limits.MaxFilesPerMessage.ShouldBe(_settings.MaxFilesPerMessage);
        limits.AllowedMediaTypes.ShouldContain("application/pdf");
    }

    [Fact]
    public async Task CreateAttachmentDownload_MintsAShortLivedUrlForTheOwningSpace()
    {
        var reference = await StoreAsync("default");

        var download = _hub.CreateAttachmentDownload(reference.Id);

        download.ShouldNotBeNull();
        download.Url.ShouldStartWith($"/api/attachments/{reference.Id}?ticket=");
        download.ExpiresAt.ShouldBe(_time.GetUtcNow().AddSeconds(_settings.TicketTtlSeconds));
    }

    // The registration gate its sibling CreateUploadTicket already enforces: a download URL is a
    // read over the upload store, so an anonymous connection gets neither direction.
    [Fact]
    public async Task CreateAttachmentDownload_BeforeRegistering_IsRefused()
    {
        var reference = await StoreAsync("default");
        _hub.Context.Items.Remove("UserId");

        Should.Throw<HubException>(() => _hub.CreateAttachmentDownload(reference.Id));
    }

    [Fact]
    public async Task CreateAttachmentDownload_FromAnotherSpace_IsRefused()
    {
        var reference = await StoreAsync("private");

        _hub.CreateAttachmentDownload(reference.Id).ShouldBeNull();
    }

    [Fact]
    public void CreateAttachmentDownload_ForAFileThatIsGone_IsRefused()
    {
        _hub.CreateAttachmentDownload("7-42/deadbeef").ShouldBeNull();
    }

    // The references are what the agent is woken with. Bytes never ride the hub, so if they do
    // not reach the notification the turn has nothing to hydrate.
    [Fact]
    public async Task TheAttachmentList_ReachesTheChannelNotification()
    {
        var reference = await StoreAsync("default");

        // Broadcast discards an item with no subscriber registered, so the agent's pump has to
        // have polled once before the emit for this to be about the notification at all.
        await ReceiveAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in _hub.SendMessage(TopicId, "look", null, null, [reference], cts.Token))
        {
        }

        var notification = (await ReceiveAsync())
            .Select(item => item.Message)
            .OfType<ChannelMessageNotification>()
            .ShouldHaveSingleItem();

        notification.Attachments.ShouldNotBeNull();
        notification.Attachments!.Single().FileName.ShouldBe("photo.png");
    }

    private Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync() =>
        _inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);

    private async Task<AttachmentReference> StoreAsync(string spaceSlug)
    {
        using var content = new MemoryStream("bytes"u8.ToArray());
        return await _store.SaveAsync("7:42", spaceSlug, "photo.png", "image/png", content, CancellationToken.None);
    }
}