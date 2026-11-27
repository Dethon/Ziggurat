using Shouldly;
using WebChat.Client.Components.Chat;

namespace Tests.Unit.WebChat.Client.Components;

public sealed class ImageViewerTests
{
    private static IReadOnlyList<ViewedPicture> Three =>
    [
        new("a", "a.png", "/a"), new("b", "b.png", null), new("c", "c.png", "/c")
    ];

    [Fact]
    public void Opening_ShowsThePictureChosen_AndTellsTheHost()
    {
        var viewer = new ImageViewer();
        var told = 0;
        viewer.Changed += () => told++;

        viewer.Open(Three, 1);

        viewer.Current!.AttachmentId.ShouldBe("b");
        (viewer.Position, viewer.Count, told).ShouldBe((2, 3, 1));
    }

    [Fact]
    public void Stepping_GoesRoundTheMessagesPictures()
    {
        var viewer = new ImageViewer();
        viewer.Open(Three, 0);

        viewer.Step(-1);
        viewer.Current!.AttachmentId.ShouldBe("c");
        viewer.Step(1);
        viewer.Current!.AttachmentId.ShouldBe("a");
    }

    [Fact]
    public void Closing_ShowsNothing()
    {
        var viewer = new ImageViewer();
        viewer.Open(Three, 2);

        viewer.Close();

        viewer.Current.ShouldBeNull();
    }

    [Fact]
    public void APlaceOutsideTheList_ShowsTheFirst()
    {
        var viewer = new ImageViewer();

        viewer.Open(Three, 7);

        viewer.Current!.AttachmentId.ShouldBe("a");
    }
}