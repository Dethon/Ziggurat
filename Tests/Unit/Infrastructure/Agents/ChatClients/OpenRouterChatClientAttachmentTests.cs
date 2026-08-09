using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// Hydration puts the bytes back where the reference sits, on the way to the model and never on
// the way in. What the client was handed is left alone, because the extraction worker reads the
// persisted copy back as the user's own words and the history must not grow a second copy of
// something already on disk.
public class OpenRouterChatClientAttachmentTests
{
    private readonly Mock<IChatClient> _innerClient = new();

    private static readonly byte[] _bytes = [1, 2, 3, 4];

    private static readonly AttachmentReference _photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    private static readonly AttachmentReference _document = new()
    {
        Id = "7-42/def",
        FileName = "scan.pdf",
        MediaType = "application/pdf",
        SizeBytes = 4
    };

    [Fact]
    public async Task AnImageOnTheUserTurn_ReachesTheModelBesideTheText()
    {
        var captured = await SendAsync(UserTurnWith(_photo));

        var user = captured.Last(m => m.Role == ChatRole.User);
        user.Contents.OfType<TextContent>().ShouldNotBeEmpty();
        var data = user.Contents.OfType<DataContent>().ShouldHaveSingleItem();
        data.MediaType.ShouldBe("image/png");
        data.Data.ToArray().ShouldBe(_bytes);
    }

    [Fact]
    public async Task APdfOnTheUserTurn_ReachesTheModelTheSameWay()
    {
        var captured = await SendAsync(UserTurnWith(_document));

        var data = captured.Last(m => m.Role == ChatRole.User).Contents
            .OfType<DataContent>().ShouldHaveSingleItem();
        data.MediaType.ShouldBe("application/pdf");
    }

    [Fact]
    public async Task SeveralAttachmentsOnOneTurn_AllReachTheModel()
    {
        var captured = await SendAsync(UserTurnWith(_photo, _document));

        captured.Last(m => m.Role == ChatRole.User).Contents.OfType<DataContent>().Count().ShouldBe(2);
    }

    [Fact]
    public async Task TheMessagesTheClientWasHanded_AreNeverWrittenBackWithBytes()
    {
        var turn = UserTurnWith(_photo);

        await SendAsync(turn);

        turn.Contents.OfType<DataContent>().ShouldBeEmpty();
        turn.GetAttachments().ShouldBe([_photo]);
    }

    [Fact]
    public async Task AClientWithNoAttachmentSource_SendsTheTurnUnchanged()
    {
        var captured = await SendAsync(UserTurnWith(_photo), NoSource);

        captured.Last(m => m.Role == ChatRole.User).Contents.OfType<DataContent>().ShouldBeEmpty();
    }

    private static readonly IAttachmentSource? NoSource = null;

    private static ChatMessage UserTurnWith(params AttachmentReference[] attachments)
    {
        var message = new ChatMessage(ChatRole.User, "what is in this?");
        message.SetAttachments(attachments);
        message.SetAttachmentChannelId("signalr");
        return message;
    }

    private async Task<IReadOnlyList<ChatMessage>> SendAsync(
        ChatMessage turn, IAttachmentSource? source = null, bool useStub = true)
    {
        IReadOnlyList<ChatMessage> captured = [];
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (msgs, _, _) => captured = msgs.ToList())
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        var sut = new OpenRouterChatClient(
            _innerClient.Object,
            "test-model",
            attachmentSource: source ?? (useStub ? new StubAttachmentSource(_bytes) : null));

        await foreach (var _ in sut.GetStreamingResponseAsync([turn]))
        {
        }

        return captured;
    }

    private async Task<IReadOnlyList<ChatMessage>> SendAsync(ChatMessage turn, IAttachmentSource? source)
        => await SendAsync(turn, source, useStub: false);

    private sealed class StubAttachmentSource(byte[] bytes) : IAttachmentSource
    {
        public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct)
            => Task.FromResult<byte[]?>(bytes);
    }
}