using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Domain.Tools.Scheduling.Vfs;
using Domain.Tools.Sugary.Watches;
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

    // A path as a session resolved it: /watchers is a union whose own name is neither member's, so each
    // member is asked; a mount of its own is asked by its name.
    [Theory]
    [InlineData("laura-sugar-high/watch.json", true)]
    [InlineData("laura-sugar-high/status.json", false)]
    public void AResolvedPath_DelegatesAsItsMountsWouldWrite(string path, bool delegates)
    {
        var watchers = new UnionFileSystem(
        [
            new UnionFileSystem.Member("ha-watches", new FileSystemUnion("/triggers/*/trigger", [], Fallback: true), new Named(HaWatchFileSystem.Name)),
            new UnionFileSystem.Member("sugary-watches", new FileSystemUnion("/triggers/*/trigger", ["glucose"]), new Named(SugaryWatchFileSystem.Name))
        ]);

        DelegatedWrites.Delegates(new FileSystemResolution(watchers, path, "/watchers")).ShouldBe(delegates);
        DelegatedWrites.Delegates(new FileSystemResolution(new Named(HaWatchFileSystem.Name), path, "/watchers")).ShouldBe(delegates);
        DelegatedWrites.Delegates(new FileSystemResolution(new Named("vault"), path, "/vault")).ShouldBeFalse();
    }

    // Only a built app's ask, which acts on the apps channel, may leave nothing a later turn acts on.
    [Fact]
    public void AnAppsAsk_MayLeaveNoLaterTurn_AndAnyOtherTurnMay()
    {
        DelegatedWrites.MayBeLeftBy(IdentityClaims.Person("fran", "household", AppChannels.Apps, ["member"])).ShouldBeFalse();
        DelegatedWrites.MayBeLeftBy(IdentityClaims.Person("fran", "household", "signalr", ["member"])).ShouldBeTrue();
        DelegatedWrites.MayBeLeftBy(null).ShouldBeTrue();
    }

    private sealed class Named(string name) : FileSystemBackendBase
    {
        public override string FilesystemName => name;

        public override string DescribeMount => name;
    }

    [Fact]
    public void TheHomeMount_ReadsNothingBeforeACreate() =>
        DelegatedWrites.CreateReadsFirst(HaFileSystem.Name, overwrite: true).ShouldBeFalse();

    private const string GlucoseWatch = """
        {"name":"Low","triggers":[{"trigger":"glucose","below":70}],
         "effects":[{"kind":"prompt","prompt":"Tell her she is low."},{"kind":"announce","text":"Glucose is low"}]}
        """;

    // A glucose watch sits at /watchers beside the home's, on Sugary's own filesystem: a write there is
    // signed as what Sugary will fire — the writing turn's agent, its prompts, and the delivery the write
    // resolves to — described exactly as Sugary describes the watch it then keeps.
    [Theory]
    [InlineData("low/watch.json")]
    [InlineData("/low/watch.json")]
    public void AGlucoseWatchFile_Delegates_AsSugaryDescribesTheWatchItKeeps(string path)
    {
        DelegatedWrites.Delegates(SugaryWatchFileSystem.Name, path).ShouldBeTrue();
        DelegatedWrites.CreateReadsFirst(SugaryWatchFileSystem.Name, overwrite: true).ShouldBeTrue();

        var what = DelegatedWrites.Of(SugaryWatchFileSystem.Name, path, GlucoseWatch, null, _writer, TimeZoneInfo.Utc)
            .ShouldNotBeNull();

        what.Kind.ShouldBe(DelegatedObject.Watch);
        what.Id.ShouldBe("low");
        what.Description.ShouldBe(GlucoseWatchDelegation.Of(new StoredGlucoseWatch
        {
            Id = "low",
            SubjectUserId = "fran",
            WriterUserId = "fran",
            AgentId = "jonas",
            Spec = GlucoseWatchSpec.Parse(GlucoseWatch).ToJson(),
            DeliverTo = ["telegram"]
        }).Description);
        DelegatedObject.Read<DelegatedWatch>(what.Description)!.Prompts.ShouldBe(["Tell her she is low."]);
    }

    [Theory]
    [InlineData("low/status.json")]
    [InlineData("low")]
    public void AnythingElseOfAGlucoseWatch_DelegatesNothing(string path)
    {
        DelegatedWrites.Delegates(SugaryWatchFileSystem.Name, path).ShouldBeFalse();
        DelegatedWrites.Of(SugaryWatchFileSystem.Name, path, GlucoseWatch, null, _writer, TimeZoneInfo.Utc).ShouldBeNull();
    }

    // A watch mount numbers the lines it answers a read with, for the model; what the agent reads before
    // an edit or a replace is the file under those numbers, or nothing it parses would be a watch.
    [Theory]
    [InlineData(SugaryWatchFileSystem.Name)]
    [InlineData(HaWatchFileSystem.Name)]
    public void AWatchReadBeforeAWrite_IsTheFileWithoutItsLineNumbers(string filesystem)
    {
        const string file = "{\n  \"name\": \"Low\"\n}";

        DelegatedWrites.FileText(filesystem, "1: {\n2:   \"name\": \"Low\"\n3: }").ShouldBe(file);
    }

    [Fact]
    public void AScheduleRead_IsTheFileAsAnswered() =>
        DelegatedWrites.FileText(ScheduleFileSystem.Name, "{\"prompt\":\"p\"}").ShouldBe("{\"prompt\":\"p\"}");
}