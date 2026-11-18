using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.Security;

// A home watch is written at /watchers/<id>/watch.json, through the home's watch filesystem; the
// delegation a write carries is keyed on that filesystem and that path, so the move cannot leave a
// watch written through the new mount firing for nobody.
public class DelegatedWritesTests
{
    private const string Watch = """
        {"name":"Laura's sugar","triggers":[{"trigger":"numeric_state","entity_id":"sensor.glucosa_laura","above":180}],
         "effects":[{"kind":"prompt","prompt":"Look into it."}],"deliverTo":["telegram"]}
        """;

    private static readonly ConversationContext _writer =
        new("jonas", "conv-1", "fran", new ReplyTarget("telegram", "conv-1"));

    [Theory]
    [InlineData("laura-sugar-high/watch.json")]
    [InlineData("/laura-sugar-high/watch.json")]
    public void AWatchFileOnTheWatchMount_Delegates(string path)
    {
        DelegatedWrites.Delegates(HaWatchFileSystem.Name, path).ShouldBeTrue();
        DelegatedWrites.CreateReadsFirst(HaWatchFileSystem.Name, overwrite: true).ShouldBeTrue();

        var what = DelegatedWrites.Of(HaWatchFileSystem.Name, path, Watch, null, _writer, TimeZoneInfo.Utc).ShouldNotBeNull();
        what.Kind.ShouldBe(DelegatedObject.Watch);
        what.Id.ShouldBe("laura-sugar-high");
    }

    [Theory]
    [InlineData(HaWatchFileSystem.Name, "laura-sugar-high/status.json")]
    [InlineData(HaWatchFileSystem.Name, "laura-sugar-high")]
    [InlineData(HaFileSystem.Name, "watches/laura-sugar-high/watch.json")]
    public void AnythingElse_DelegatesNothing(string filesystem, string path)
    {
        DelegatedWrites.Delegates(filesystem, path).ShouldBeFalse();
        DelegatedWrites.Of(filesystem, path, Watch, null, _writer, TimeZoneInfo.Utc).ShouldBeNull();
    }

    [Fact]
    public void TheHomeMount_ReadsNothingBeforeACreate() =>
        DelegatedWrites.CreateReadsFirst(HaFileSystem.Name, overwrite: true).ShouldBeFalse();
}