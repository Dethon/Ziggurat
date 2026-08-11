using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

public class DeliveryTargetResolverAnnounceTests
{
    private static readonly DeliveryTargetResolver _resolver = new([], NullLogger.Instance);

    private static (Mock<IChannelConnection> Mock, List<(string? InitialPrompt, string? Address, string? ExistingConversationId)> Calls) Channel(string id)
    {
        var calls = new List<(string?, string?, string?)>();
        var m = new Mock<IChannelConnection>();
        m.SetupGet(c => c.ChannelId).Returns(id);
        m.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? prompt, string? address, string? existing, CancellationToken _) => calls.Add((prompt, address, existing)))
            .ReturnsAsync((string _, string _, string _, string? _, string? _, string? existing, CancellationToken _) => existing);
        return (m, calls);
    }

    private static ChannelMessage DownloadMessage(string content = "[download-complete] film.mkv")
    {
        return new ChannelMessage
        {
            ConversationId = "7:42",
            Content = content,
            Sender = "fran",
            ChannelId = "library",
            AgentId = "jack",
            Origin = new MessageOrigin(MessageOriginKind.Download, null)
        };
    }

    [Fact]
    public async Task AnnounceTurnStart_PreExistingTarget_CallsCreateConversationWithExistingIdAndPrompt()
    {
        var (signalr, calls) = Channel("signalr");
        var targets = new[] { new DeliveryTarget(signalr.Object, "7:42") };

        await _resolver.AnnounceTurnStartAsync(targets, DownloadMessage(), CancellationToken.None);

        var call = calls.ShouldHaveSingleItem();
        call.ExistingConversationId.ShouldBe("7:42");
        call.InitialPrompt.ShouldBe("[download-complete] film.mkv");
    }

    [Fact]
    public async Task AnnounceTurnStart_TargetMintedByThisTurn_IsSkipped()
    {
        // The mint already called create_conversation, so announcing it again would set the
        // same stream up twice.
        var (signalr, calls) = Channel("signalr");
        var targets = new[] { new DeliveryTarget(signalr.Object, "minted-1", Minted: true) };

        await _resolver.AnnounceTurnStartAsync(targets, DownloadMessage(), CancellationToken.None);

        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnnounceTurnStart_TargetWithAddress_ThreadsAddressIntoCreateConversation()
    {
        // The address (e.g. the originating voice satellite) tells the channel WHERE the
        // turn belongs; without it an addressable channel would fall back to broadcast.
        var (voice, calls) = Channel("voice");
        var targets = new[] { new DeliveryTarget(voice.Object, "7:42", Address: "fran-office-01") };

        await _resolver.AnnounceTurnStartAsync(targets, DownloadMessage(), CancellationToken.None);

        calls.ShouldHaveSingleItem().Address.ShouldBe("fran-office-01");
    }

    [Fact]
    public async Task AnnounceTurnStart_AnnounceThrows_SwallowsAndContinuesToNextTarget()
    {
        var failing = new Mock<IChannelConnection>();
        failing.SetupGet(c => c.ChannelId).Returns("signalr");
        failing.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        var (telegram, telegramCalls) = Channel("telegram");
        var targets = new[]
        {
            new DeliveryTarget(failing.Object, "7:42"),
            new DeliveryTarget(telegram.Object, "t-9")
        };

        await Should.NotThrowAsync(
            _resolver.AnnounceTurnStartAsync(targets, DownloadMessage(), CancellationToken.None));

        telegramCalls.ShouldHaveSingleItem();
    }
}