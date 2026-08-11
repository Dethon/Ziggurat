using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

public class DeliveryTargetResolverTests
{
    private static IChannelConnection Channel(string id)
    {
        var m = new Mock<IChannelConnection>();
        m.SetupGet(c => c.ChannelId).Returns(id);
        m.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? _, string? _, string? _, CancellationToken _) => $"minted-{id}");
        return m.Object;
    }

    private static DeliveryTargetResolver Resolver(params IChannelConnection[] channels)
    {
        return new DeliveryTargetResolver(channels, NullLogger.Instance);
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WithoutReplyTo_DeliversToOrigin()
    {
        var origin = Channel("signalr");
        var channels = new[] { origin, Channel("telegram") };
        var msg = new ChannelMessage { ConversationId = "c1", Content = "x", Sender = "u", ChannelId = "signalr" };

        var targets = await Resolver(channels).ResolveAsync(msg, origin, CancellationToken.None);

        var t = targets.ShouldHaveSingleItem();
        t.Channel.ChannelId.ShouldBe("signalr");
        t.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WithReplyTo_FansOutAndMintsMissingConversations()
    {
        var origin = Channel("scheduling");
        var channels = new[] { origin, Channel("signalr"), Channel("telegram") };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "x",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-9")]
        };

        var targets = await Resolver(channels).ResolveAsync(msg, origin, CancellationToken.None);

        targets.Count.ShouldBe(2);
        targets[0].ConversationId.ShouldBe("minted-signalr");
        targets[1].ConversationId.ShouldBe("t-9");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WithUnknownChannelInReplyTo_SkipsIt()
    {
        var origin = Channel("scheduling");
        var channels = new[] { origin, Channel("signalr") };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1", Content = "x", Sender = "s", ChannelId = "scheduling",
            ReplyTo = [new ReplyTarget("does-not-exist", "z")]
        };

        var targets = await Resolver(channels).ResolveAsync(msg, origin, CancellationToken.None);

        targets.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveDeliveryTargets_DownloadCompletion_UsesConcreteConversationWithoutMinting()
    {
        var origin = Channel("library");
        var signalr = new FakeChannelConnection { ChannelId = "signalr" };
        var msg = new ChannelMessage
        {
            ConversationId = "conv-7", Content = "[download-complete] ...", Sender = "fran",
            ChannelId = "library", AgentId = "jack",
            Origin = new MessageOrigin(MessageOriginKind.Download, null),
            ReplyTo = [new ReplyTarget("signalr", "conv-7")]
        };

        var targets = await Resolver(origin, signalr).ResolveAsync(msg, origin, CancellationToken.None);

        var target = targets.ShouldHaveSingleItem();
        target.ConversationId.ShouldBe("conv-7");
        target.Channel.ChannelId.ShouldBe("signalr");
        signalr.CreatedConversations.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WhenMintingConversation_PassesMessageContentAsInitialPrompt()
    {
        // A scheduled fire delivers to WebChat with a null ReplyTo conversationId,
        // so a new conversation is minted. The minted channel must receive the
        // schedule's actual prompt text as `initialPrompt`, otherwise WebChat
        // displays the topic name ("Scheduled task") in place of the user bubble.
        var origin = Channel("scheduling");
        var captor = new Mock<IChannelConnection>();
        captor.SetupGet(c => c.ChannelId).Returns("signalr");
        string? capturedPrompt = null;
        captor.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? p, string? _, string? _, CancellationToken _) => capturedPrompt = p)
            .ReturnsAsync("minted-signalr");
        var channels = new[] { origin, captor.Object };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "Check qBittorrent for stalled torrents",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null)]
        };

        await Resolver(channels).ResolveAsync(msg, origin, CancellationToken.None);

        capturedPrompt.ShouldBe("Check qBittorrent for stalled torrents");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WhenMintingThrows_SkipsTargetInsteadOfThrowing()
    {
        var origin = Channel("scheduling");
        var failing = new Mock<IChannelConnection>();
        failing.SetupGet(c => c.ChannelId).Returns("signalr");
        failing.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        var channels = new[] { origin, failing.Object };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1", Content = "x", Sender = "s", ChannelId = "scheduling",
            ReplyTo = [new ReplyTarget("signalr", null)]
        };

        var targets = await Resolver(channels).ResolveAsync(msg, origin, CancellationToken.None);

        targets.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveDeliveryTargets_ThreadsReplyTargetAddressIntoCreateConversation()
    {
        var origin = Channel("scheduling");
        var captor = new Mock<IChannelConnection>();
        captor.SetupGet(c => c.ChannelId).Returns("voice");
        string? capturedAddress = null;
        captor.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? _, string? a, string? _, CancellationToken _) => capturedAddress = a)
            .ReturnsAsync("minted-voice");
        var channels = new[] { origin, captor.Object };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "The AC is on",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "mycroft",
            ReplyTo = [new ReplyTarget("voice", null, "fran-office-01")]
        };

        await Resolver(channels).ResolveAsync(msg, origin, CancellationToken.None);

        capturedAddress.ShouldBe("fran-office-01");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_AttachOnlyOrderingIsCapabilityDriven_NotChannelIdDriven()
    {
        // Anchoring must key off the config-declared AttachOnly capability, not any channel
        // id literal: a future attach-only channel with an arbitrary id is ordered last and
        // never anchors, while nothing in the agent names "voice".
        var origin = Channel("scheduling");
        var signalr = Channel("signalr");
        var notify = new Mock<IChannelConnection>();
        notify.SetupGet(c => c.ChannelId).Returns("notify");
        notify.SetupGet(c => c.AttachOnly).Returns(true);
        notify.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? _, string? _, string? existing, CancellationToken _) => existing ?? "minted-notify");
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1", Content = "x", Sender = "s", ChannelId = "scheduling",
            ReplyTo = [new ReplyTarget("notify", null), new ReplyTarget("signalr", null)]
        };

        var targets = await Resolver(origin, signalr, notify.Object).ResolveAsync(msg, origin, CancellationToken.None);

        targets.Count.ShouldBe(2);
        targets[0].Channel.ChannelId.ShouldBe("signalr");
        targets.ShouldAllBe(t => t.ConversationId == "minted-signalr");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_MarksMintedTargetsAndPreservesPreExistingOnes()
    {
        var origin = Channel("scheduling");
        var channels = new[] { origin, Channel("signalr"), Channel("telegram") };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "x",
            Sender = "s",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-9")]
        };

        var targets = await Resolver(channels).ResolveAsync(msg, origin, CancellationToken.None);

        targets.Count.ShouldBe(2);
        targets.Single(t => t.Channel.ChannelId == "signalr").Minted.ShouldBeTrue();
        targets.Single(t => t.Channel.ChannelId == "telegram").Minted.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveDeliveryTargets_CarriesReplyTargetAddressOnDeliveryTargets()
    {
        // The address must survive resolution so the per-message turn-start announce can
        // tell an addressable channel (voice) which satellite the turn belongs to.
        var origin = Channel("library");
        var voice = Channel("voice");
        var msg = new ChannelMessage
        {
            ConversationId = "conv-9", Content = "x", Sender = "fran", ChannelId = "library",
            ReplyTo = [new ReplyTarget("voice", "conv-9", "fran-office-01")]
        };

        var targets = await Resolver(origin, voice).ResolveAsync(msg, origin, CancellationToken.None);

        targets.ShouldHaveSingleItem().Address.ShouldBe("fran-office-01");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WithoutReplyTo_OriginTargetIsNotMinted()
    {
        var origin = Channel("signalr");
        var msg = new ChannelMessage { ConversationId = "c1", Content = "x", Sender = "u", ChannelId = "signalr" };

        var targets = await Resolver(origin).ResolveAsync(msg, origin, CancellationToken.None);

        targets.ShouldHaveSingleItem().Minted.ShouldBeFalse();
    }
}