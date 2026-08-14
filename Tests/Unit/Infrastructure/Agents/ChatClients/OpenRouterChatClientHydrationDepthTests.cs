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

    // A sandbox path is not a byte: the file stays in the sandbox long after the bytes stop
    // being sent, so the model keeps being told where it is.
    [Fact]
    public async Task ASandboxPath_ReachesTheModelAndOutlivesTheHydrationDistance()
    {
        var messages = Conversation(attachmentAt: 0, length: 5);
        messages[0].SetSandboxPaths(["/sandbox/uploads/7-42/turn/photo.png"]);

        var captured = await SendAsync(messages, depth: 1);

        var text = string.Join("", captured[0].Contents.OfType<TextContent>().Select(c => c.Text));
        text.ShouldContain("/sandbox/uploads/7-42/turn/photo.png");
        captured[0].Contents.OfType<DataContent>().ShouldBeEmpty();
        messages[0].Contents.OfType<TextContent>().ShouldAllBe(c => !c.Text.Contains("/sandbox/"));
    }

    // A file that could not be put in the sandbox is the same kind of claim as the bytes, with the
    // same boundary: the model is told which files it lost, in the turn it lost them, so its first
    // answer accounts for it rather than planning commands against paths that do not exist.
    [Fact]
    public async Task AFileThatCouldNotBeLanded_IsNamedToTheModelWithinTheDistance()
    {
        var messages = Conversation(attachmentAt: 4, length: 5);
        messages[4].SetLandingFailures(["ledger.csv"]);

        var captured = await SendAsync(messages, depth: 3);

        string.Join("", captured[4].Contents.OfType<TextContent>().Select(c => c.Text))
            .ShouldContain("ledger.csv");
    }

    // Past the distance the model has neither the bytes nor the file, and a notice about neither is
    // noise — unlike a landed path, which keeps naming a file that is still there.
    [Fact]
    public async Task AFileThatCouldNotBeLanded_IsNotMentionedBeyondTheDistance()
    {
        var messages = Conversation(attachmentAt: 0, length: 5);
        messages[0].SetLandingFailures(["ledger.csv"]);

        var captured = await SendAsync(messages, depth: 3);

        string.Join("", captured[0].Contents.OfType<TextContent>().Select(c => c.Text))
            .ShouldNotContain("ledger.csv");
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

    // The function-calling client re-sends the whole list once per tool iteration, growing it by
    // a call and a result each time. Counting those would push an attachment out of its own turn
    // partway through and tell the model the file it was just given is gone.
    [Fact]
    public async Task ToolCallsAddedDuringTheTurn_DoNotPushAnAttachmentOutOfItsOwnTurn()
    {
        var messages = new List<ChatMessage>
        {
            WithAttachment(new ChatMessage(ChatRole.User, "look at this"), _photo)
        };
        messages.AddRange(Enumerable.Range(0, 12).SelectMany(i => new[]
        {
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{i}", "search", null)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", "a result")])
        }));

        var captured = await SendAsync(messages, depth: 3);

        captured[0].Contents.OfType<DataContent>().ShouldHaveSingleItem();
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