using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
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

// A dictation ticket is space-scoped rather than topic-scoped and counts no slot: dictating
// produces composer text, so it must not force a conversation into existence the way picking a
// file does. The browser also learns the recording rules where it already learns the file rules.
public sealed class ChatHubDictationTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly AttachmentSettings _attachmentSettings = new() { TicketTtlSeconds = 60 };
    private readonly DictationSettings _dictation = new()
    {
        MaxLength = TimeSpan.FromMinutes(3),
        MinLength = TimeSpan.FromMilliseconds(250)
    };

    private readonly AttachmentTickets _tickets;
    private readonly StreamService _streamService;
    private readonly ChatHub _hub;

    public ChatHubDictationTests()
    {
        _tickets = new AttachmentTickets(_attachmentSettings, _time);
        var sessionService = new SessionService();
        _streamService = new StreamService(
            sessionService,
            new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);

        _hub = new ChatHub(
            sessionService,
            _streamService,
            approvalService: null!,
            new ChannelNotificationEmitter(new ChannelInbox(_time), DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object,
            threadStore: null!,
            pushSubscriptionStore: null!,
            new AttachmentService(
                _attachmentSettings,
                _tickets,
                new AttachmentStore(_attachmentSettings, _time, NullLogger<AttachmentStore>.Instance),
                NullLogger<AttachmentService>.Instance),
            new Mock<IHubNotificationSender>().Object,
            new RetentionSettings(),
            _dictation,
            NullLogger<ChatHub>.Instance)
        {
            Context = new RegisteredCaller()
        };
    }

    public void Dispose() => _streamService.Dispose();

    // No session and no topic: a dictation must be possible on a screen where no conversation has
    // been started yet, because the words land in the composer and not in a conversation.
    [Fact]
    public void CreateDictationTicket_NeedsNoTopicAndResolvesForTheCallersSpace()
    {
        var ticket = _hub.CreateDictationTicket();

        ticket.ExpiresAt.ShouldBe(_time.GetUtcNow().AddSeconds(_attachmentSettings.TicketTtlSeconds));
        _tickets.ResolvesDictation(ticket.Token, "default").ShouldBeTrue();
        _tickets.ResolvesDictation(ticket.Token, "private").ShouldBeFalse();
    }

    [Fact]
    public void CreateDictationTicket_BeforeRegistering_IsRefused()
    {
        _hub.Context.Items.Remove("UserId");

        Should.Throw<HubException>(() => _hub.CreateDictationTicket());
    }

    // Changing the cap or the floor must not need new JavaScript shipped, so the browser learns
    // both where it already learns the attachment rules.
    [Fact]
    public void GetAttachmentLimits_CarriesTheRecordingCapAndTheMisTapFloor()
    {
        var limits = _hub.GetAttachmentLimits();

        limits.MaxDictationMs.ShouldBe(180_000);
        limits.MinDictationMs.ShouldBe(250);
    }
}