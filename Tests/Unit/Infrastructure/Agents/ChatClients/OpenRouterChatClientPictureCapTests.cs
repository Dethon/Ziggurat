using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// An attachment stays in front of the model for the whole conversation: swapping a picture for a
// placeholder rewrites the prompt at that picture and bills everything after it again uncached, and a
// picture already in the cached prefix costs a tenth of its tokens (user, 2026-11-27). The one bound is
// the providers' own: past the newest MaxPicturesPerRequest pictures of a request, older ones go as
// placeholders naming the file, so a long conversation never fails its turns on a provider's cap.
public class OpenRouterChatClientPictureCapTests
{
    private readonly Mock<IChatClient> _innerClient = new();

    private static readonly AttachmentReference _photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    private static readonly AttachmentReference _newerPhoto = new()
    {
        Id = "7-42/ghi",
        FileName = "newer.png",
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
    public async Task AnAttachmentFarBackInTheConversation_StillReachesTheModelAsContent()
    {
        var captured = await SendAsync(Conversation(attachmentAt: 0, length: 60));

        captured[0].Contents.OfType<DataContent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task APicturePastTheCap_ReachesTheModelAsAPlaceholderNamingTheFile()
    {
        var messages = Conversation(attachmentAt: 0, length: 5);
        messages[2] = WithAttachment(new ChatMessage(ChatRole.User, "and this one"), _newerPhoto);

        var captured = await SendAsync(messages, maxPictures: 1);

        captured[2].Contents.OfType<DataContent>().ShouldHaveSingleItem();
        captured[0].Contents.OfType<DataContent>().ShouldBeEmpty();
        var text = string.Join("", captured[0].Contents.OfType<TextContent>().Select(c => c.Text));
        text.ShouldContain("photo.png");
        text.ShouldContain("no longer available");
    }

    // The cap is the providers' count of images; a document is not one, so it is always sent.
    [Fact]
    public async Task ADocument_IsSentWhateverTheCap()
    {
        var captured = await SendAsync(
            Conversation(attachmentAt: 0, length: 5, attachment: _document), maxPictures: 0);

        captured[0].Contents.OfType<DataContent>().ShouldHaveSingleItem().MediaType.ShouldBe("application/pdf");
    }

    // With pictures kept for the whole conversation, one sent long ago is still in every request, and
    // the wire rejects a whole request carrying an image its model cannot take. A turn moved to a model
    // that takes none is told which earlier pictures it is not shown, and runs.
    [Fact]
    public async Task AnEarlierPhoto_OnAModelThatTakesNoImages_IsNamedNotSent()
    {
        var captured = await SendAsync(Conversation(attachmentAt: 0, length: 5), acceptsImages: false);

        captured[0].Contents.OfType<DataContent>().ShouldBeEmpty();
        var text = string.Join("", captured[0].Contents.OfType<TextContent>().Select(c => c.Text));
        text.ShouldContain("photo.png");
        text.ShouldContain("does not accept images");
    }

    [Fact]
    public async Task TheCapDefaultsToAHundredPictures()
    {
        var messages = Enumerable.Range(0, 101)
            .Select(i => WithAttachment(new ChatMessage(ChatRole.User, $"photo {i}"), _photo))
            .ToList();

        var captured = await SendAsync(messages);

        captured[0].Contents.OfType<DataContent>().ShouldBeEmpty();
        captured[1].Contents.OfType<DataContent>().ShouldHaveSingleItem();
        AttachmentHydration.DefaultMaxPictures.ShouldBe(100);
    }

    // A sandbox path is not a byte: the file stays in the sandbox after its picture stops being
    // sent, so the model keeps being told where it is.
    [Fact]
    public async Task ASandboxPath_ReachesTheModelAndOutlivesItsPicture()
    {
        var messages = Conversation(attachmentAt: 0, length: 5);
        messages[0].SetSandboxPaths(["/sandbox/uploads/7-42/turn/photo.png"]);

        var captured = await SendAsync(messages, maxPictures: 0);

        var text = string.Join("", captured[0].Contents.OfType<TextContent>().Select(c => c.Text));
        text.ShouldContain("/sandbox/uploads/7-42/turn/photo.png");
        captured[0].Contents.OfType<DataContent>().ShouldBeEmpty();
        messages[0].Contents.OfType<TextContent>().ShouldAllBe(c => !c.Text.Contains("/sandbox/"));
    }

    // A photo the model can already see, named only by its sandbox path, read as an errand: jonas
    // described the picture and then file_read it, a call and the image a second time (prod,
    // 2026-10-18). Shown first and named as shown, the path is left for what it is for — a command.
    [Fact]
    public async Task ASandboxPath_ForAFileShownInTheMessage_FollowsThePictureAndSaysItIsShown()
    {
        var messages = Conversation(attachmentAt: 0, length: 1);
        messages[0].SetSandboxPaths(["/sandbox/uploads/7-42/turn/photo.png"]);

        var captured = await SendAsync(messages);

        var contents = captured[0].Contents;
        var note = contents.OfType<TextContent>().Single(c => c.Text.Contains("/sandbox/uploads/"));
        contents.IndexOf(contents.OfType<DataContent>().Single()).ShouldBeLessThan(contents.IndexOf(note));
        note.Text.ShouldBe(AttachmentLanding.Describe(["/sandbox/uploads/7-42/turn/photo.png"], shown: true));
    }

    // Where the bytes did not go, the sandbox copy is all the model has, and nothing may tell it the
    // file is in front of it.
    [Fact]
    public async Task ASandboxPath_ForAFileNotShown_IsNamedAsTheOnlyCopy()
    {
        var messages = Conversation(attachmentAt: 0, length: 1);
        messages[0].SetSandboxPaths(["/sandbox/uploads/7-42/turn/photo.png"]);

        var captured = await SendAsync(messages, source: new EmptyAttachmentSource());

        var note = AttachmentLanding.Describe(["/sandbox/uploads/7-42/turn/photo.png"], shown: false);
        captured[0].Contents.OfType<TextContent>().Select(c => c.Text).ShouldContain(note);
    }

    // A file that could not be put in the sandbox is named to the model so its answers account for
    // it rather than planning commands against paths that do not exist — whatever became of the
    // message's pictures.
    [Fact]
    public async Task AFileThatCouldNotBeLanded_IsNamedToTheModelWhateverTheCap()
    {
        var messages = Conversation(attachmentAt: 0, length: 5);
        messages[0].SetLandingFailures(["ledger.csv"]);

        var captured = await SendAsync(messages, maxPictures: 0);

        string.Join("", captured[0].Contents.OfType<TextContent>().Select(c => c.Text))
            .ShouldContain("ledger.csv");
    }

    [Fact]
    public async Task AReferenceWhoseFileIsGone_ProducesTheSamePlaceholder()
    {
        var captured = await SendAsync(
            Conversation(attachmentAt: 4, length: 5), source: new EmptyAttachmentSource());

        captured[4].Contents.OfType<DataContent>().ShouldBeEmpty();
        string.Join("", captured[4].Contents.OfType<TextContent>().Select(c => c.Text))
            .ShouldContain("photo.png");
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
        IReadOnlyList<ChatMessage> messages, int? maxPictures = null, IAttachmentSource? source = null,
        bool acceptsImages = true)
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

        var sut = maxPictures is null
            ? new OpenRouterChatClient(
                _innerClient.Object, "test-model",
                attachmentSource: source ?? new StubAttachmentSource(),
                modelAcceptsImages: _ => acceptsImages)
            : new OpenRouterChatClient(
                _innerClient.Object, "test-model",
                attachmentSource: source ?? new StubAttachmentSource(),
                maxPicturesPerRequest: maxPictures.Value,
                modelAcceptsImages: _ => acceptsImages);

        await foreach (var _ in sut.GetStreamingResponseAsync(messages))
        {
        }

        return captured;
    }

    private sealed class StubAttachmentSource : IAttachmentSource
    {
        public Task<byte[]?> FetchAsync(
            string channelId, string? householdId, string attachmentId, ConversationContext? turn, CancellationToken ct)
            => Task.FromResult<byte[]?>([1, 2, 3, 4]);
    }

    private sealed class EmptyAttachmentSource : IAttachmentSource
    {
        public Task<byte[]?> FetchAsync(
            string channelId, string? householdId, string attachmentId, ConversationContext? turn, CancellationToken ct)
            => Task.FromResult<byte[]?>(null);
    }
}