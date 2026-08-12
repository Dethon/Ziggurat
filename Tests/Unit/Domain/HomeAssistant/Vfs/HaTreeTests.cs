using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaTreeTests
{
    private static HaCatalog Cat() => new(
        [Entity("light.kitchen", "off"), Entity("sensor.salon_temp", "21")],
        [Service("light", "turn_on", AnyEntityTarget())],
        [new HaAreaEntities("salon", "Salón", ["sensor.salon_temp"])]);

    [Fact]
    public void DirectoriesAndFiles_IncludeRootsClassesEntitiesAreas_AndApplicableActions()
    {
        var cat = Cat();
        var dirs = HaTree.Directories(cat);
        var files = HaTree.Files(cat);

        dirs.ShouldContain("entities");
        dirs.ShouldContain("entities/light");
        dirs.ShouldContain("entities/light/kitchen");
        dirs.ShouldContain("areas");
        dirs.ShouldContain("areas/salon");
        dirs.ShouldContain("areas/salon/sensor.salon_temp");
        dirs.ShouldContain("areas/unassigned/light.kitchen");

        files.ShouldContain("entities/light/kitchen/state.json");
        files.ShouldContain("entities/light/kitchen/turn_on.sh");
        files.ShouldContain("entities/sensor/salon_temp/state.json");
        files.ShouldNotContain("entities/sensor/salon_temp/turn_on.sh"); // no actions for sensor
    }

    [Fact]
    public void DirectoriesAndFiles_UseCompositeNameWhenFriendlyNamePresent()
    {
        var cat = new HaCatalog(
            [
                Entity("climate.0x00158d00abcd", "cool", ("friendly_name", JsonValue.Create("Aire Acondicionado Salón"))),
                Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen Light")))
            ],
            [Service("light", "turn_on", AnyEntityTarget())],
            [new HaAreaEntities("salon", "Salón", ["climate.0x00158d00abcd"])]);

        var dirs = HaTree.Directories(cat);
        var files = HaTree.Files(cat);

        dirs.ShouldContain("entities/climate/0x00158d00abcd_(aire-acondicionado-salon)");
        dirs.ShouldContain("areas/salon/climate.0x00158d00abcd_(aire-acondicionado-salon)");
        files.ShouldContain("entities/light/kitchen_(kitchen-light)/state.json");
        files.ShouldContain("entities/light/kitchen_(kitchen-light)/turn_on.sh");
    }

    [Fact]
    public void Files_ExposesCrossDomainServiceWithQualifiedName_NoCollision()
    {
        var cat = new HaCatalog(
            [Entity("media_player.office", "idle")],
            [
                Service("media_player", "play_media", DomainTarget("media_player")),
                Service("music_assistant", "play_media", DomainTarget("media_player"))
            ],
            []);

        var files = HaTree.Files(cat);

        files.ShouldContain("entities/media_player/office/play_media.sh");
        files.ShouldContain("entities/media_player/office/music_assistant.play_media.sh");
    }

    [Fact]
    public void Glob_TrailingSlash_ReturnsDirectoriesOnly_MarkedWithSlash()
    {
        HaTree.Glob(Cat(), Scope("entities/light", "*/")).ShouldBe(["entities/light/kitchen/"]);
    }

    [Fact]
    public void Glob_NoTrailingSlash_ReturnsBothDirectoriesAndFiles()
    {
        var hits = HaTree.Glob(Cat(), Scope("entities/light", "**"));

        hits.ShouldContain("entities/light/kitchen/");
        hits.ShouldContain("entities/light/kitchen/state.json");
        hits.ShouldContain("entities/light/kitchen/turn_on.sh");
    }

    [Fact]
    public void Glob_DoubleStar_MatchesZeroLeadingSegments_AndStillRecurses()
    {
        // `**/X` must match X at the base level (zero leading segments), matching the Local file
        // matcher. An older `** -> .*` translation required a leading '/', missing the base-level
        // `entities` directory — guard against regressions here.
        var cat = Cat();

        var baseLevel = HaTree.Glob(cat, Scope("", "**/entities"));
        baseLevel.ShouldContain("entities/");

        var recursive = HaTree.Glob(cat, Scope("", "**/state.json"));
        recursive.ShouldContain("entities/light/kitchen/state.json");
        recursive.ShouldContain("entities/sensor/salon_temp/state.json");

        var shFiles = HaTree.Glob(cat, Scope("entities", "**/*.sh"));
        shFiles.ShouldNotBeEmpty();
        shFiles.ShouldAllBe(h => h.EndsWith(".sh"));
    }

    private static GlobScope Scope(string basePath, string pattern) =>
        GlobScope.Create(basePath, pattern).ShouldBeOfType<FsResult<GlobScope>.Ok>().Value;
}