using Domain.Tools.Downloads.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.Downloads.Vfs;

public class DownloadsPathTests
{
    [Theory]
    [InlineData("downloads/42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("/downloads/42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("downloads/42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("/downloads/42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("downloads/-42", DownloadNodeKind.DownloadDir, -42)]
    [InlineData("downloads/-1120736916/status.json", DownloadNodeKind.StatusFile, -1120736916)]
    [InlineData("", DownloadNodeKind.Other, null)]
    [InlineData("/", DownloadNodeKind.Other, null)]
    [InlineData("downloads", DownloadNodeKind.Other, null)]
    [InlineData("downloads/foo", DownloadNodeKind.Other, null)]
    [InlineData("downloads/42/payload.mkv", DownloadNodeKind.Other, null)]
    [InlineData("Movies/42", DownloadNodeKind.Other, null)]
    [InlineData("../downloads/42", DownloadNodeKind.Other, null)]
    public void Parse_ClassifiesPath_ReturnsKindAndId(string path, DownloadNodeKind kind, int? id)
    {
        var node = DownloadsPath.Parse(path);
        node.Kind.ShouldBe(kind);
        node.Id.ShouldBe(id);
    }

    // The disk underneath resolves '.' and '..' before it touches a file, so a path spelled with
    // them lands on exactly the node the overlay owns. Classifying the literal spelling instead let
    // 'downloads/42/./status.json' miss every overlay rule while the disk wrote the real file at
    // downloads/42/status.json — invisible afterwards and unremovable.
    [Theory]
    [InlineData("downloads/42/./status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("./downloads/42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("downloads/./42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("downloads/43/../42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("downloads/42/..", DownloadNodeKind.Other, null)]
    [InlineData("Movies/../downloads/42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("downloads/42/../../downloads/42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("downloads/../../downloads/42", DownloadNodeKind.Other, null)]
    public void Parse_DotSegments_AreResolvedBeforeClassifying(string path, DownloadNodeKind kind, int? id)
    {
        var node = DownloadsPath.Parse(path);
        node.Kind.ShouldBe(kind);
        node.Id.ShouldBe(id);
    }

    // The overlay owns exactly the ids the download manager hands out: an int spelled the way
    // int.ToString spells it — a minus for negatives (ids are link hash codes), no plus, no padding,
    // no surrounding blanks. Every other spelling is a real directory name on disk, and shadowing it
    // with a virtual status file — or cancelling download 42 when asked to delete it — is not the
    // overlay's call.
    [Theory]
    [InlineData("downloads/ 42 ")]
    [InlineData("downloads/042")]
    [InlineData("downloads/042/status.json")]
    [InlineData("downloads/+42")]
    [InlineData("downloads/-042")]
    [InlineData("downloads/-0")]
    [InlineData("downloads/4 2")]
    [InlineData("downloads/99999999999999999999")]
    public void Parse_IdThatIsNotItsOwnDigits_IsPlainDisk(string path)
    {
        var node = DownloadsPath.Parse(path);
        node.Kind.ShouldBe(DownloadNodeKind.Other);
        node.Id.ShouldBeNull();
    }

    [Theory]
    [InlineData("downloads/42/./status.json", "downloads/42/status.json")]
    [InlineData("/downloads/./42", "downloads/42")]
    [InlineData("downloads/43/../42", "downloads/42")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void Canonicalize_ResolvesDotSegments(string path, string expected) =>
        DownloadsPath.Canonicalize(path).ShouldBe(expected);

    [Fact]
    public void Canonicalize_PathClimbingAboveTheMountRoot_IsNull() =>
        DownloadsPath.Canonicalize("../downloads/42").ShouldBeNull();
}