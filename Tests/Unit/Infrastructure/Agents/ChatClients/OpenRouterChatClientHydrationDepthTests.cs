using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// An attachment stays visible to the model long enough for follow-up questions to work, and then
// stops. Beyond that distance the model is told plainly that the file is gone, by a placeholder
// naming it, so it says so rather than inventing what the file contained. The transcript is
// unaffected: those are different lifetimes on purpose (ADR 0020).
public class OpenRouterChatClientHydrationDepthTests
{
    private readonly Mock<IChatClient> _innerClient = new();

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
    public async Task AnAttachmentWithinTheDepth_ReachesTheModelAsContent()
    {
        var captured = await SendAsync(Conversation(attachmentAt: 3, length: 5), depth: 3);

        captured[3].Contents.OfType<DataContent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnAttachmentBeyondTheDepth_ReachesTheModelAsAPlaceholderNamingTheFile()
    {
        var captured = await SendAsync(Conversation(attachmentAt: 0, length: 5), depth: 3);

        captured[0].Contents.OfType<DataContent>().ShouldBeEmpty();
        var text = string.Join("", captured[0].Contents.OfType<TextContent>().Select(c => c.Text));
        text.ShouldContain("photo.png");
        text.ShouldContain("no longer available");
    }

    [Fact]
    public async Task TheDepthAppliesToEveryKindOfAttachment()
    {
        var messages = Conversation(attachmentAt: 0, length: 5, attachment: _document);

        var captured = await SendAsync(messages, depth: 3);

        captured[0].Contents.OfType<DataContent>().ShouldBeEmpty();
        string.Join("", captured[0].Contents.OfType<TextContent>().Select(c => c.Text))
            .ShouldContain("scan.pdf");
    }

    [Fact]
    public async Task AReferenceWhoseFileIsGone_ProducesTheSamePlaceholderAtAnyDistance()
    {
        var captured = await SendAsync(
            Conversation(attachmentAt: 4, length: 5), depth: 20, source: new EmptyAttachmentSource());

        captured[4].Contents.OfType<DataContent>().ShouldBeEmpty();
        string.Join("", captured[4].Contents.OfType<TextContent>().Select(c => c.Text))
            .ShouldContain("photo.png");
    }

    [Fact]
    public async Task TheDepthDefaultsToTwentyMessages()
    {
        // Twenty-one messages: only the oldest is out of reach.
        var messages = Conversation(attachmentAt: 0, length: 21);
        messages[1] = WithAttachment(new ChatMessage(ChatRole.User, "and this one"), _photo);

        var captured = await SendAsync(messages);

        captured[0].Contents.OfType<DataContent>().ShouldBeEmpty();
        captured[1].Contents.OfType<DataContent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ThePlaceholder_IsNeverWrittenBackIntoTheMessagesTheClientWasHanded()
    {
        var messages = Conversation(attachmentAt: 0, length: 5);

        await SendAsync(messages, depth: 3);

        messages[0].Contents.OfType<TextContent>()
            .ShouldAllBe(c => !c.Text.Contains("no longer available"));
    }

    private static List<ChatMessage> Conversation(
        int attachmentAt, int length, AttachmentReference? attachment = null)
    {
        return Enumerable.Range(0, length)
            .Select(i => i == attachmentAt
                ? WithAttachment(new ChatMessage(ChatRole.User, "look at this"), attachment ?? _photo)
                : new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"message {i}"))
            .ToList();
    }

    private static ChatMessage WithAttachment(ChatMessage message, AttachmentReference attachment)
    {
        message.SetAttachments([attachment]);
        message.SetAttachmentChannelId("signalr");
        return message;
    }

    private async Task<IReadOnlyList<ChatMessage>> SendAsync(
        IReadOnlyList<ChatMessage> messages, int? depth = null, IAttachmentSource? source = null)
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

        var sut = depth is null
            ? new OpenRouterChatClient(
                _innerClient.Object, "test-model",
                attachmentSource: source ?? new StubAttachmentSource())
            : new OpenRouterChatClient(
                _innerClient.Object, "test-model",
                attachmentSource: source ?? new StubAttachmentSource(),
                hydrationDepthMessages: depth.Value);

        await foreach (var _ in sut.GetStreamingResponseAsync(messages))
        {
        }

        return captured;
    }

    private sealed class StubAttachmentSource : IAttachmentSource
    {
        public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct)
            => Task.FromResult<byte[]?>([1, 2, 3, 4]);
    }

    private sealed class EmptyAttachmentSource : IAttachmentSource
    {
        public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct)
            => Task.FromResult<byte[]?>(null);
    }
}