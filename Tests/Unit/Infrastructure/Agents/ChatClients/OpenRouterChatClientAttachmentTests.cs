using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Security.Signing;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
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
        var captured = await SendAsync(UserTurnWith(_photo), source: null);

        captured.Last(m => m.Role == ChatRole.User).Contents.OfType<DataContent>().ShouldBeEmpty();
    }

    // A household's own server's file is asked of that household's connection, which the turn named.
    [Fact]
    public async Task AFileAHouseholdsOwnServerTook_IsAskedOfThatHouseholdsConnection()
    {
        var turn = UserTurnWith(_photo);
        turn.SetAttachmentChannelId("voice");
        turn.SetAttachmentHouseholdId("casa");
        var source = new HouseholdRecordingSource();

        await SendAsync(turn, source);

        source.Asked.ShouldBe([("voice", "casa", _photo.Id)]);
    }

    // Every file the send puts back, an earlier turn's included, is fetched for the turn being sent: the one
    // the agent stamped on the request's options, whose identity the fetch is signed as.
    [Fact]
    public async Task EveryFile_IsFetchedForTheTurnBeingSent_AnEarlierMessagesToo()
    {
        var earlier = UserTurnWith(_photo);
        var now = new ChatMessage(ChatRole.User, "and this one?");
        now.SetAttachments([_document]);
        now.SetAttachmentChannelId("signalr");
        var asking = new ConversationContext("jonas", "7:42", "her", new ReplyTarget("apps", "7:42"))
        {
            Identity = IdentityClaims.Person("her", "household-a", "apps", [])
        };
        var source = new HouseholdRecordingSource();

        await SendAsync([earlier, new ChatMessage(ChatRole.Assistant, "A cat."), now], source,
            new ChatOptions { AdditionalProperties = new() { [ConversationContextMeta.OptionsKey] = asking } });

        source.Turns.ShouldBe([asking, asking]);
    }

    private static ChatMessage UserTurnWith(params AttachmentReference[] attachments)
    {
        var message = new ChatMessage(ChatRole.User, "what is in this?");
        message.SetAttachments(attachments);
        message.SetAttachmentChannelId("signalr");
        return message;
    }

    private Task<IReadOnlyList<ChatMessage>> SendAsync(ChatMessage turn)
        => SendAsync(turn, new StubAttachmentSource(_bytes));

    private Task<IReadOnlyList<ChatMessage>> SendAsync(ChatMessage turn, IAttachmentSource? source) =>
        SendAsync([turn], source, null);

    private async Task<IReadOnlyList<ChatMessage>> SendAsync(
        IReadOnlyList<ChatMessage> messages, IAttachmentSource? source, ChatOptions? options)
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
            attachmentSource: source);

        await foreach (var _ in sut.GetStreamingResponseAsync(messages, options))
        {
        }

        return captured;
    }

    private sealed class HouseholdRecordingSource : IAttachmentSource
    {
        public List<(string ChannelId, string? HouseholdId, string AttachmentId)> Asked { get; } = [];

        public List<ConversationContext?> Turns { get; } = [];

        public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct) =>
            FetchAsync(channelId, null, attachmentId, null, ct);

        public Task<byte[]?> FetchAsync(
            string channelId, string? householdId, string attachmentId, ConversationContext? turn, CancellationToken ct)
        {
            Asked.Add((channelId, householdId, attachmentId));
            Turns.Add(turn);
            return Task.FromResult<byte[]?>(_bytes);
        }
    }

    private sealed class StubAttachmentSource(byte[] bytes) : IAttachmentSource
    {
        public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct)
            => Task.FromResult<byte[]?>(bytes);
    }
}