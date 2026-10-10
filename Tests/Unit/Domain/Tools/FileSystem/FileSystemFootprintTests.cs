using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// What each filesystem tool says a call to it touches, asked of the tools a session is really
// offered. Two calls of one message conflict — and so run in the order written — when what they
// touch overlaps and one of them may change it.
public class FileSystemFootprintTests
{
    private readonly IReadOnlyDictionary<string, AIFunction> _tools = ToolsOver(bridged: true);

    [Fact]
    public void EveryFilesystemTool_DeclaresWhatItsCallsTouch() =>
        _tools.Values
            .Where(tool => tool.GetService<FootprintDeclaration>() is null)
            .Select(tool => tool.Name)
            .ShouldBeEmpty();

    // The race this exists for: the removal answered "deleted", the create answered
    // already_exists, and the timer was gone.
    [Fact]
    public void RemovingADirectory_ConflictsWithCreatingAFileUnderIt() =>
        Conflict(
            Call("remove", ("path", "/timers/pasta")),
            Call("text_create", ("filePath", "/timers/pasta/timer.json"))).ShouldBeTrue();

    [Fact]
    public void WritesToUnrelatedPaths_DoNotConflict() =>
        Conflict(
            Call("text_create", ("filePath", "/vault/a.md")),
            Call("text_edit", ("filePath", "/vault/b.md"))).ShouldBeFalse();

    [Fact]
    public void AReadOfOneFile_DoesNotConflictWithAnEditOfAnother() =>
        Conflict(
            Call("file_read", ("filePath", "/vault/a.md")),
            Call("text_edit", ("filePath", "/vault/b.md"))).ShouldBeFalse();

    [Fact]
    public void AReadOfAFile_ConflictsWithAnEditOfIt() =>
        Conflict(
            Call("text_edit", ("filePath", "/vault/a.md")),
            Call("file_read", ("filePath", "/vault/a.md"))).ShouldBeTrue();

    [Theory]
    [InlineData("file_read", "filePath")]
    [InlineData("file_info", "path")]
    [InlineData("glob", "basePath")]
    [InlineData("text_search", "directoryPath")]
    public void ReadsOfOnePath_DoNotConflictWithEachOther(string tool, string argument) =>
        Conflict(
            Call(tool, (argument, "/vault/notes")),
            Call("file_read", ("filePath", "/vault/notes"))).ShouldBeFalse();

    // A glob and a search read a whole subtree: a write anywhere under it changes their answer.
    [Theory]
    [InlineData("glob", "basePath")]
    [InlineData("text_search", "directoryPath")]
    public void AWalkOfADirectory_ConflictsWithAWriteAnywhereUnderIt_AndWithNoneOutsideIt(string tool, string argument)
    {
        var walk = Call(tool, (argument, "/vault/notes"));

        Conflict(walk, Call("text_create", ("filePath", "/vault/notes/2026/today.md"))).ShouldBeTrue();
        Conflict(walk, Call("remove", ("path", "/vault"))).ShouldBeTrue();
        Conflict(walk, Call("text_create", ("filePath", "/vault/journal/today.md"))).ShouldBeFalse();
    }

    [Fact]
    public void ASearchOfOneFile_ReadsThatFileAlone()
    {
        var search = Call("text_search", ("filePath", "/vault/notes/a.md"), ("directoryPath", "/vault"));

        Conflict(search, Call("text_edit", ("filePath", "/vault/notes/a.md"))).ShouldBeTrue();
        Conflict(search, Call("text_edit", ("filePath", "/vault/notes/b.md"))).ShouldBeFalse();
    }

    [Fact]
    public void AMove_ChangesBothItsPaths()
    {
        var move = Call("move", ("sourcePath", "/vault/a.md"), ("destinationPath", "/sandbox/home/a.md"));

        Conflict(move, Call("file_read", ("filePath", "/vault/a.md"))).ShouldBeTrue();
        Conflict(move, Call("file_read", ("filePath", "/sandbox/home/a.md"))).ShouldBeTrue();
        Conflict(move, Call("file_read", ("filePath", "/vault/b.md"))).ShouldBeFalse();
    }

    [Fact]
    public void ACopy_ReadsItsSourceAndChangesItsDestination()
    {
        var copy = Call("copy", ("sourcePath", "/vault/a.md"), ("destinationPath", "/vault/b.md"));

        Conflict(copy, Call("file_read", ("filePath", "/vault/a.md"))).ShouldBeFalse();
        Conflict(copy, Call("text_edit", ("filePath", "/vault/a.md"))).ShouldBeTrue();
        Conflict(copy, Call("file_read", ("filePath", "/vault/b.md"))).ShouldBeTrue();
    }

    // The registry repairs a forgotten slash and matches a mount point in any case, so the
    // footprint has to: two spellings the tools treat as one path are one resource.
    [Theory]
    [InlineData("/vault/notes/a.md", "vault/notes/a.md")]
    [InlineData("/vault/notes/a.md", "/VAULT/notes/a.md")]
    [InlineData("/vault/notes/", "/vault/notes")]
    [InlineData("/vault//notes/./a.md", "/vault/notes/a.md")]
    [InlineData("outpost:laptop/home/me/a.md", "OUTPOST:Laptop/home/me/a.md")]
    public void TwoSpellingsOfOnePath_AreOneResource(string one, string other) =>
        Conflict(
            Call("text_edit", ("filePath", one)),
            Call("file_read", ("filePath", other))).ShouldBeTrue();

    [Fact]
    public void AMachineAddress_IsNotTheMountOfTheSameName() =>
        Conflict(
            Call("text_edit", ("filePath", "outpost:vault/a.md")),
            Call("file_read", ("filePath", "/vault/a.md"))).ShouldBeFalse();

    // Where `..` lands is the backend's to decide, so the claim widens to the mount.
    [Fact]
    public void APathThatClimbs_ClaimsItsWholeMount()
    {
        var climbing = Call("text_edit", ("filePath", "/vault/notes/../journal/a.md"));

        Conflict(climbing, Call("file_read", ("filePath", "/vault/journal/a.md"))).ShouldBeTrue();
        Conflict(climbing, Call("file_read", ("filePath", "/sandbox/home/a.md"))).ShouldBeFalse();
    }

    [Theory]
    [InlineData("/nowhere/a.md")]
    [InlineData("")]
    [InlineData(null)]
    public void APathNoMountServes_OrNoPathAtAll_ClaimsTheWholeFilesystem(string? path) =>
        Conflict(
            Call("text_edit", ("filePath", path)),
            Call("file_read", ("filePath", "/vault/a.md"))).ShouldBeTrue();

    // A model's arguments arrive as JSON, a host's as strings; the path is the same path.
    [Fact]
    public void APathWrittenAsJson_IsReadLikeAString()
    {
        using var json = JsonDocument.Parse("\"/vault/a.md\"");

        Conflict(
            Call("text_edit", ("filePath", json.RootElement.Clone())),
            Call("file_read", ("filePath", "/vault/b.md"))).ShouldBeFalse();
    }

    // A sandbox command reaches every served mount through the bridge, and nothing in its text
    // says which: it is taken as changing all of them.
    [Theory]
    [InlineData("/sandbox/home")]
    [InlineData("/vault/notes")]
    public void ACommandThatRunsInTheSandbox_ConflictsWithEveryFilesystemCall(string cwd)
    {
        var exec = Call("exec", ("path", cwd), ("command", "sed -i s/a/b/ *.md"));

        Conflict(exec, Call("file_read", ("filePath", "/vault/a.md"))).ShouldBeTrue();
        Conflict(exec, Call("file_read", ("filePath", "/timers/pasta/timer.json"))).ShouldBeTrue();
        Conflict(exec, Call("exec", ("path", "/sandbox/home"), ("command", "ls"))).ShouldBeTrue();
    }

    [Fact]
    public void ACommandOnSomebodysOwnMachine_ChangesThatMachineAlone()
    {
        var exec = Call("exec", ("path", "outpost:laptop/home/me"), ("command", "make"));

        Conflict(exec, Call("file_read", ("filePath", "outpost:laptop/etc/hosts"))).ShouldBeTrue();
        Conflict(exec, Call("file_read", ("filePath", "/vault/a.md"))).ShouldBeFalse();
    }

    // One action out of a mount's closed catalog: two lights switched in one message have
    // nothing to wait for in each other, and a read of the light just switched does.
    [Fact]
    public void AnActionFileRunByItself_ChangesOnlyWhereItStands()
    {
        var kitchen = Call("exec", ("path", "/ha/light.kitchen"), ("command", "./turn_on"));

        Conflict(kitchen, Call("exec", ("path", "/ha/light.hall"), ("command", "./turn_on"))).ShouldBeFalse();
        Conflict(kitchen, Call("file_read", ("filePath", "/ha/light.kitchen/state.json"))).ShouldBeTrue();
        Conflict(kitchen, Call("exec", ("path", "/ha/light.kitchen"), ("command", "./turn_off"))).ShouldBeTrue();
    }

    [Fact]
    public void WithNoSandboxToRunIn_AMountsOwnExec_ChangesOnlyWhereItStands()
    {
        var tools = ToolsOver(bridged: false);
        var dismiss = FootprintDeclaration.Of(tools["exec"], Arguments(("path", "/timers/pasta"), ("command", "./dismiss && echo done")));
        var other = FootprintDeclaration.Of(tools["file_read"], Arguments(("filePath", "/timers/eggs/timer.json")));

        dismiss.ConflictsWith(other).ShouldBeFalse();
    }

    private bool Conflict(CallFootprint one, CallFootprint other)
    {
        // A conflict has no direction: whichever call was written first, the other waits.
        one.ConflictsWith(other).ShouldBe(other.ConflictsWith(one));
        return one.ConflictsWith(other);
    }

    private CallFootprint Call(string tool, params (string Name, object? Value)[] arguments) =>
        FootprintDeclaration.Of(_tools[tool], Arguments(arguments));

    private static Dictionary<string, object?> Arguments(params (string Name, object? Value)[] arguments) =>
        arguments.ToDictionary(a => a.Name, a => a.Value);

    private static IReadOnlyDictionary<string, AIFunction> ToolsOver(bool bridged)
    {
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount("vault", "/vault", "v"), Mock.Of<IFileSystemBackend>());
        registry.Mount(new FileSystemMount("timers", "/timers", "t"), Mock.Of<IFileSystemBackend>());
        registry.Mount(new FileSystemMount("ha", "/ha", "h"), Mock.Of<IFileSystemBackend>());
        registry.Mount(new FileSystemMount("sandbox", "/sandbox", "s") { ShellReach = ShellReach.Contained }, Mock.Of<IFileSystemBackend>());
        registry.Mount(new FileSystemMount("laptop", "outpost:laptop", "l") { ShellReach = ShellReach.Host }, Mock.Of<IFileSystemBackend>());
        registry.Mount(new FileSystemMount("vault", "outpost:vault", "another vault"), Mock.Of<IFileSystemBackend>());

        return new FileSystemToolFeature(registry, bridge: bridged ? Mock.Of<IVfsBridge>() : null)
            .GetTools(new FeatureConfig())
            .ToDictionary(tool => tool.Name["domain__filesystem__".Length..]);
    }
}