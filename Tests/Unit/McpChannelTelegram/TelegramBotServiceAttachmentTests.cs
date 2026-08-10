using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// A photo or a document reaches the model: the addressing rule text uses today, with the caption
// standing in for it, and a reference naming the bot that can fetch the bytes back.
public class TelegramBotServiceAttachmentTests : IDisposable
{
    private readonly TelegramPollingHarness _harness = new();

    [Fact]
    public async Task PhotoWithAQualifyingCaption_EmitsTheCaptionAndOneReference()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask what is this");
        message.Photo = TelegramPollingHarness.Photo("AgACphoto");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        var batch = await _harness.ReceiveAsync();
        var notification = batch.ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Content.ShouldBe("/ask what is this");
        var attachment = notification.Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
        attachment.Id.ShouldBe("jack/AgACphoto");
        attachment.MediaType.ShouldBe("image/jpeg");
        attachment.FileName.ShouldBe("attachment-10.jpg");
        attachment.SizeBytes.ShouldBe(2048);
    }

    [Fact]
    public async Task DocumentWithAQualifyingCaption_KeepsTheFilenameTelegramCarried()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask summarise this");
        message.Document = TelegramPollingHarness.Document(fileId: "BQACdoc", fileName: "quarterly-report.pdf");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        var batch = await _harness.ReceiveAsync();
        var attachment = batch.ShouldHaveSingleItem().Message!.Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
        attachment.Id.ShouldBe("jack/BQACdoc");
        attachment.FileName.ShouldBe("quarterly-report.pdf");
        attachment.MediaType.ShouldBe("application/pdf");
    }

    // A person showing the agent something and letting it respond. Qualifying is the forum thread,
    // because the addressing rule is unchanged and a caption is what the command prefix rides on.
    [Fact]
    public async Task AttachmentsWithNoCaption_ProduceATurnWithEmptyContent()
    {
        var message = TelegramPollingHarness.MediaMessage(threadId: 42);
        message.Photo = TelegramPollingHarness.Photo();

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        var batch = await _harness.ReceiveAsync();
        var notification = batch.ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Content.ShouldBe(string.Empty);
        notification.Attachments.ShouldNotBeNull().Count.ShouldBe(1);
    }

    [Fact]
    public async Task MediaThatDoesNotQualifyUnderTheAddressingRule_IsStillIgnored()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "look at this");
        message.Photo = TelegramPollingHarness.Photo();

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).ShouldBeEmpty();
        _harness.Sent.ShouldBeEmpty();
    }

    // A client that describes a PDF vaguely must not cost someone their file over a technicality.
    [Fact]
    public async Task ADocumentWithAGenericMimeType_ResolvesByItsExtension()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask read it");
        message.Document = TelegramPollingHarness.Document(
            fileName: "scan.pdf", mimeType: "application/octet-stream");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        var batch = await _harness.ReceiveAsync();
        batch.ShouldHaveSingleItem().Message!.Attachments
            .ShouldNotBeNull().ShouldHaveSingleItem().MediaType.ShouldBe("application/pdf");
    }

    // Keeping the original quality means sending the picture as a file; it is still a picture.
    [Fact]
    public async Task AnImageSentAsAFile_ResolvesToAnImage()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask what is this");
        message.Document = TelegramPollingHarness.Document(
            fileId: "BQACimg", fileName: "IMG_0042.PNG", mimeType: "image/png");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        var batch = await _harness.ReceiveAsync();
        var attachment = batch.ShouldHaveSingleItem().Message!.Attachments
            .ShouldNotBeNull().ShouldHaveSingleItem();
        attachment.MediaType.ShouldBe("image/png");
        attachment.FileName.ShouldBe("IMG_0042.PNG");
    }

    // Refusals arrive in their own ticket; here an unresolvable file is simply not an attachment.
    [Fact]
    public async Task MediaWhoseKindResolvesToNothing_IsNotAnAttachment()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask look at this");
        message.Video = new Video
        {
            FileId = "BAACvid",
            FileUniqueId = "u-vid",
            Width = 640,
            Height = 480,
            Duration = 5,
            MimeType = "video/mp4"
        };

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        var batch = await _harness.ReceiveAsync();
        batch.ShouldHaveSingleItem().Message!.Attachments.ShouldBeNull();
    }

    public void Dispose() => _harness.Dispose();
}