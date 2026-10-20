using Domain.DTOs.Channel;
using Shouldly;
using WebChat.Client.Components.Chat;

namespace Tests.Unit.WebChat.Client.Components;

// What a message's attachments look like in the transcript. A filmed meal's review topic shows what
// a video model was sent: the clip, played where it sits, and her photos — never the stills taken
// from the clip, which WebChat is not even given a copy of (ADR 0046).
public sealed class AttachmentDisplayTests
{
    private static readonly AttachmentReference _photo = Reference("11-22/photo", "meal-1.jpg", "image/jpeg");
    private static readonly AttachmentReference _still1 = Reference("11-22/s1", "still-1-0.4s.jpg", "image/jpeg");
    private static readonly AttachmentReference _still2 = Reference("11-22/s2", "still-2-2.9s.jpg", "image/jpeg");
    private static readonly AttachmentReference _pdf = Reference("11-22/doc", "scan.pdf", "application/pdf");

    private static readonly AttachmentReference _clip = Reference("11-22/clip", "meal.mp4", "video/mp4") with
    {
        Stills = [_still1.Id, _still2.Id]
    };

    [Fact]
    public void AFilmedMeal_ShowsItsClipAsAClipAndItsPhotos_AndNoneOfTheClipsStills()
    {
        var shown = AttachmentDisplay.For([_photo, _clip, _still1, _still2]);

        shown.ShouldBe([
            new ShownAttachment(_photo, AttachmentShape.Thumbnail),
            new ShownAttachment(_clip, AttachmentShape.Clip)
        ]);
    }

    // Stills are only a clip's when a clip names them; with no clip on the message every picture is
    // somebody's photo and shows as one.
    [Fact]
    public void PicturesNoClipNames_ShowAsBefore()
    {
        var shown = AttachmentDisplay.For([_photo, _still1, _pdf]);

        shown.ShouldBe([
            new ShownAttachment(_photo, AttachmentShape.Thumbnail),
            new ShownAttachment(_still1, AttachmentShape.Thumbnail),
            new ShownAttachment(_pdf, AttachmentShape.Chip)
        ]);
    }

    // A thumbnail and a clip both need a download URL to render at all; a chip mints one only when
    // clicked.
    [Fact]
    public void ThumbnailsAndClips_AreTheOnesPreviewedAsTheyRender()
    {
        var shown = AttachmentDisplay.For([_photo, _clip, _pdf]);

        AttachmentDisplay.Previewed(shown).ShouldBe([_photo.Id, _clip.Id]);
    }

    [Fact]
    public void NoAttachments_ShowNothing() => AttachmentDisplay.For(null).ShouldBeEmpty();

    private static AttachmentReference Reference(string id, string fileName, string mediaType) =>
        new() { Id = id, FileName = fileName, MediaType = mediaType, SizeBytes = 1 };
}