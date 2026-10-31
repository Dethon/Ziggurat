using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class CreateConversationToolTests
{
    private readonly VoiceDeliveryRegistry _delivery;
    private readonly VoiceConversationManager _manager;
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly IServiceProvider _services;

    public CreateConversationToolTests()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["office-01"] = new() { Identity = "household", Room = "Office" },
            ["office-02"] = new() { Identity = "household", Room = "Office" }
        });
        _delivery = new VoiceDeliveryRegistry(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            new ReplyTextAccumulator(),
            NullLogger<VoiceDeliveryRegistry>.Instance);

        _manager = new VoiceConversationManager(
            new FakeTopicMinter(), new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        _services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(_delivery)
            .AddSingleton(_manager)
            .AddSingleton(_sessions)
            .BuildServiceProvider();
    }

    // A delivery speaks into the conversation it is handed: the fan-out's anchor, or the topic the
    // agent minted for a fire only voice delivers.
    private const string Conversation = "11:22";

    [Fact]
    public void McpRun_AllAddress_BindsAllTarget()
    {
        var convId = CreateConversationTool.McpRun(_services, "all", Conversation);

        _delivery.Resolve(convId)!.All.ShouldBe(true);
    }

    [Fact]
    public void McpRun_NullAddress_BindsAllTarget()
    {
        var convId = CreateConversationTool.McpRun(_services, null, Conversation);

        _delivery.Resolve(convId)!.All.ShouldBe(true);
    }

    [Fact]
    public void McpRun_UnknownSatellite_Throws()
    {
        Should.Throw<McpException>(() => CreateConversationTool.McpRun(_services, "ghost-99", Conversation));
    }

    [Fact]
    public void McpRun_MultipleSatellites_BindsAllOfThem()
    {
        // A schedule delivering to several specific satellites (deliverTo coalesces them into a
        // comma-joined address) must speak on every one — bound as a single AnnounceTarget with
        // a satellite-id set, not just the last.
        var convId = CreateConversationTool.McpRun(_services, "office-01,office-02", Conversation);

        var target = _delivery.Resolve(convId);
        target.ShouldNotBeNull();
        target!.SatelliteIds.ShouldBe(["office-01", "office-02"]);
    }

    [Fact]
    public void McpRun_MultipleSatellites_OneUnknown_Throws()
    {
        Should.Throw<McpException>(() => CreateConversationTool.McpRun(_services, "office-01,ghost-99", Conversation));
    }

    // Voice keeps no topics, so a delivery handed no conversation has nothing to attach to: it is
    // refused rather than spoken into a conversation no topic holds.
    [Fact]
    public void McpRun_NoConversationToAttachTo_Throws()
    {
        Should.Throw<McpException>(() => CreateConversationTool.McpRun(_services, "office-01"));
    }

    [Fact]
    public void McpRun_WithExistingConversationId_Attaches()
    {
        // When a scheduled delivery targets both a WebChat channel and voice, the WebChat
        // channel mints the conversation and the voice channel ATTACHES to that same id
        // for TTS routing.
        var convId = CreateConversationTool.McpRun(_services, "office-01", "shared-123");

        convId.ShouldBe("shared-123");
        _delivery.Resolve("shared-123")!.SatelliteId.ShouldBe("office-01");
    }

    [Fact]
    public async Task McpRun_AttachWithLiveSatelliteSession_DoesNotBind()
    {
        // A turn-start announce for a conversation a satellite is actively holding needs
        // no binding: send_reply routes through the live session, and a stray binding's
        // expiry would flush the shared accumulator mid-turn.
        var session = new SatelliteSession("office-01", new SatelliteConfig { Identity = "household", Room = "Office" });
        var convId = await _manager.GetOrCreateAsync(session, "mycroft", CancellationToken.None);
        _sessions.Register(session);

        var result = CreateConversationTool.McpRun(_services, "office-01", convId);

        result.ShouldBe(convId);
        _delivery.Resolve(convId).ShouldBeNull();
    }

    [Fact]
    public async Task McpRun_AttachWithDisconnectedSatellite_BindsForAnnouncement()
    {
        // The conversation is still mapped to its satellite but the session is gone
        // (disconnected): the turn is delivered as an announcement on that satellite.
        var session = new SatelliteSession("office-01", new SatelliteConfig { Identity = "household", Room = "Office" });
        var convId = await _manager.GetOrCreateAsync(session, "mycroft", CancellationToken.None);

        var result = CreateConversationTool.McpRun(_services, "office-01", convId);

        result.ShouldBe(convId);
        _delivery.Resolve(convId)!.SatelliteId.ShouldBe("office-01");
    }
}