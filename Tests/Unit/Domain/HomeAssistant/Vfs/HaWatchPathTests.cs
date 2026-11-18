using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaWatchPathTests
{
    [Theory]
    [InlineData("", HaWatchKind.Root, null)]
    [InlineData("/", HaWatchKind.Root, null)]
    [InlineData("laura-sugar-high", HaWatchKind.WatchDir, "laura-sugar-high")]
    [InlineData("/laura-sugar-high/", HaWatchKind.WatchDir, "laura-sugar-high")]
    [InlineData("laura-sugar-high/watch.json", HaWatchKind.WatchFile, "laura-sugar-high")]
    [InlineData("/laura-sugar-high/watch.json", HaWatchKind.WatchFile, "laura-sugar-high")]
    [InlineData("laura-sugar-high/status.json", HaWatchKind.WatchStatusFile, "laura-sugar-high")]
    [InlineData("laura-sugar-high/other.json", HaWatchKind.Unknown, null)]
    [InlineData("a/b/c", HaWatchKind.Unknown, null)]
    public void Parse_WatchShapes(string path, HaWatchKind kind, string? watchId)
    {
        var n = HaWatchPath.Parse(path);
        n.Kind.ShouldBe(kind);
        n.WatchId.ShouldBe(watchId);
    }
}