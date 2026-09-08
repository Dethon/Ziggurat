using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaVfsPathTests
{
    [Theory]
    [InlineData("", HaVfsKind.Root, null, null, null, null)]
    [InlineData("entities", HaVfsKind.EntitiesRoot, null, null, null, null)]
    [InlineData("areas", HaVfsKind.AreasRoot, null, null, null, null)]
    [InlineData("entities/light", HaVfsKind.ClassDir, "light", null, null, null)]
    [InlineData("entities/light/kitchen", HaVfsKind.EntityDir, "light", null, "kitchen", null)]
    [InlineData("areas/salon", HaVfsKind.AreaDir, null, "salon", null, null)]
    [InlineData("areas/salon/light.salon", HaVfsKind.EntityDir, null, "salon", "light.salon", null)]
    [InlineData("entities/light/kitchen/state.json", HaVfsKind.StateFile, "light", null, "kitchen", null)]
    [InlineData("entities/light/kitchen/turn_on.sh", HaVfsKind.ActionFile, "light", null, "kitchen", "turn_on")]
    [InlineData("areas/salon/light.salon/toggle.sh", HaVfsKind.ActionFile, null, "salon", "light.salon", "toggle")]
    // Composite (friendly-name) segments are kept raw and still parse to the right kind
    [InlineData("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)",
        HaVfsKind.EntityDir, "climate", null, "0x00158d00abcd_(aire-acondicionado-salon)", null)]
    [InlineData("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)/state.json",
        HaVfsKind.StateFile, "climate", null, "0x00158d00abcd_(aire-acondicionado-salon)", null)]
    [InlineData("areas/salon/climate.0x00158d00abcd_(aire-acondicionado-salon)/turn_off.sh",
        HaVfsKind.ActionFile, null, "salon", "climate.0x00158d00abcd_(aire-acondicionado-salon)", "turn_off")]
    public void Parse_KnownShapes(
        string path, HaVfsKind kind, string? classDomain, string? area, string? entitySegment, string? service)
    {
        var n = HaVfsPath.Parse(path);
        n.Kind.ShouldBe(kind);
        n.ClassDomain.ShouldBe(classDomain);
        n.Area.ShouldBe(area);
        n.EntitySegment.ShouldBe(entitySegment);
        n.Service.ShouldBe(service);
    }

    [Theory]
    [InlineData("watches", HaVfsKind.WatchesRoot, null)]
    [InlineData("watches/laura-sugar-high", HaVfsKind.WatchDir, "laura-sugar-high")]
    [InlineData("watches/laura-sugar-high/watch.json", HaVfsKind.WatchFile, "laura-sugar-high")]
    [InlineData("watches/laura-sugar-high/status.json", HaVfsKind.WatchStatusFile, "laura-sugar-high")]
    [InlineData("watches/laura-sugar-high/other.json", HaVfsKind.Unknown, null)]
    [InlineData("watches/a/b/c", HaVfsKind.Unknown, null)]
    public void Parse_WatchShapes(string path, HaVfsKind kind, string? watchId)
    {
        var n = HaVfsPath.Parse(path);
        n.Kind.ShouldBe(kind);
        n.WatchId.ShouldBe(watchId);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("entities/light/kitchen/extra/deep")]
    [InlineData("areas/salon/light.salon/x/y")]
    public void Parse_Unknown(string path) =>
        HaVfsPath.Parse(path).Kind.ShouldBe(HaVfsKind.Unknown);
}