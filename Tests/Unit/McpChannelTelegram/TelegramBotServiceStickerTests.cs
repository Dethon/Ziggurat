using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
using Shouldly;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Tests.Unit.McpChannelTelegram;

// Someone reacting with an animated sticker is punctuating a conversation, not attaching a file, so
// expressive media that resolves to no kind is dropped in silence. A static sticker is a picture
// like any other and goes through.
public class TelegramBotServiceStickerTests : IDisposable
{
    private readonly TelegramPollingHarness _harness = new();

    [Fact]
    public async Task AStaticSticker_BecomesAnImageAttachment()
    {
        await DriveAsync(message => message.Sticker = Sticker());

        var attachment = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Attachments
            .ShouldNotBeNull().ShouldHaveSingleItem();
        attachment.Id.ShouldBe("jack/sticker-1");
        attachment.MediaType.ShouldBe("image/webp");
        attachment.FileName.ShouldBe("attachment-10.webp");
    }

    [Fact]
    public async Task AStaticSticker_IsSubjectToTheCapabilityStopLikeAnyOtherImage()
    {
        new RegisterAgentsTool(_harness.Catalog).McpRun([
            new AgentCatalogEntry(
                "jack", "Jack", null, DefaultModel: "text-only/model", DefaultModelAttachmentKinds: [])
        ]);

        await DriveAsync(message => message.Sticker = Sticker());

        (await _harness.ReceiveAsync()).ShouldBeEmpty();
        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("text-only/model");
    }

    [Fact]
    public async Task AnAnimatedSticker_IsDroppedWithNoReply()
    {
        await DriveAsync(message => message.Sticker = Sticker(isAnimated: true));

        await ShouldBeSilentlyDroppedAsync();
    }

    [Fact]
    public async Task AVideoSticker_IsDroppedWithNoReply()
    {
        await DriveAsync(message => message.Sticker = Sticker(isVideo: true));

        await ShouldBeSilentlyDroppedAsync();
    }

    [Fact]
    public async Task AnAnimation_IsDroppedWithNoReply()
    {
        await DriveAsync(message => message.Animation = new Animation
        {
            FileId = "anim-1",
            FileUniqueId = "u-anim",
            Width = 320,
            Height = 240,
            Duration = 3,
            MimeType = "video/mp4"
        });

        await ShouldBeSilentlyDroppedAsync();
    }

    [Fact]
    public async Task AMessageWhoseOnlyMediaIsDroppedExpressiveMedia_StillRunsAsATextTurnWithItsCaption()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask what do you make of this");
        message.Animation = new Animation
        {
            FileId = "anim-1",
            FileUniqueId = "u-anim",
            Width = 320,
            Height = 240,
            Duration = 3,
            MimeType = "video/mp4"
        };

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        var notification = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Content.ShouldBe("/ask what do you make of this");
        notification.Attachments.ShouldBeNull();
        _harness.Sent.ShouldBeEmpty();
    }

    // A GIF sent through Telegram is an MP4 that kept its old name. Letting the filename overrule
    // a type Telegram was specific about would ship those bytes to the model as an image.
    [Fact]
    public async Task AnAnimationWhoseFilenameLooksLikeAnImage_IsStillDropped()
    {
        await DriveAsync(message => message.Animation = new Animation
        {
            FileId = "anim-1",
            FileUniqueId = "u-anim",
            Width = 320,
            Height = 240,
            Duration = 3,
            FileName = "giphy.gif",
            MimeType = "video/mp4"
        });

        await ShouldBeSilentlyDroppedAsync();
    }

    // Attaching a video was deliberate, so it keeps the refusal a document gets.
    [Fact]
    public async Task AnUnresolvableVideo_StillDrawsTheRefusal()
    {
        await DriveAsync(message => message.Video = new Video
        {
            FileId = "vid-1",
            FileUniqueId = "u-vid",
            Width = 640,
            Height = 480,
            Duration = 5,
            FileName = "clip.mp4",
            MimeType = "video/mp4"
        });

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("clip.mp4");
    }

    public void Dispose() => _harness.Dispose();

    private async Task ShouldBeSilentlyDroppedAsync()
    {
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
        _harness.Sent.ShouldBeEmpty();
    }

    // A sticker carries no caption of its own, so a forum thread is what addresses one to the bot.
    private async Task DriveAsync(Action<Message> attach)
    {
        var message = TelegramPollingHarness.MediaMessage(threadId: 42);
        attach(message);

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();
    }

    private static Sticker Sticker(bool isAnimated = false, bool isVideo = false) => new()
    {
        FileId = "sticker-1",
        FileUniqueId = "u-sticker",
        Type = StickerType.Regular,
        Width = 512,
        Height = 512,
        IsAnimated = isAnimated,
        IsVideo = isVideo,
        FileSize = 30_000
    };
}