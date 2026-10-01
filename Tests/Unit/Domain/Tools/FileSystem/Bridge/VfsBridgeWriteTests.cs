using System.Text;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// A command's writes through the bridge, and what the exec result reports of them. Each write is
// the call the equivalent tool would make — text as a create with overwrite, anything else as the
// blob write a copy streams through — allowed only where that call would be, and logged.
public class VfsBridgeWriteTests
{
    private readonly VfsBridge _bridge = new(new FakeTimeProvider(DateTimeOffset.UtcNow));

    private readonly MemoryDisk _vault = new("vault", new Dictionary<string, string> { ["notes/todo.md"] = "old\n" });

    private readonly RenderedMount _timers = new("timers", new Dictionary<string, string>
    {
        ["eggs/status.json"] = """{"remainingSeconds":120}"""
    });

    private VfsExecTool Tool(Func<VfsCall, CancellationToken, Task> script) =>
        new(BridgeFixtures.Registry(
            (new ScriptedSandbox(_bridge, async (call, ct) =>
            {
                await script(call, ct);
                return BridgeFixtures.Ran("");
            }), ScriptedSandbox.Mount, ShellReach.Contained),
            (_vault, "/vault", null),
            (_timers, "/timers", null)), _bridge);

    private static IReadOnlyList<(string Path, string Operation, string Status)> Changes(System.Text.Json.Nodes.JsonNode result) =>
        [.. result["vfsChanges"]!.AsArray().Select(c => (
            c!["path"]!.GetValue<string>(), c["operation"]!.GetValue<string>(), c["status"]!.GetValue<string>()))];

    [Fact]
    public async Task TextOntoAnExistingFile_IsACreateWithOverwrite_AndAWrite()
    {
        var result = await Tool((call, ct) => call.WriteAsync("/vault/notes/todo.md", "new\n"u8.ToArray(), isNew: false, ct))
            .RunAsync("/sandbox", "echo new > /vault/notes/todo.md", arguments: BridgeFixtures.Whitelisted);

        _vault.Files["notes/todo.md"].ShouldBe("new\n");
        _vault.Writes.ShouldBe(["create notes/todo.md"]);
        Changes(result).ShouldBe([("/vault/notes/todo.md", VfsChange.Operations.Write, VfsChange.Statuses.Applied)]);
    }

    [Fact]
    public async Task ANewTextFile_IsLoggedAsACreate()
    {
        var result = await Tool((call, ct) => call.WriteAsync("/vault/a/b/c.md", "x\n"u8.ToArray(), isNew: true, ct))
            .RunAsync("/sandbox", "mkdir -p /vault/a/b && echo x > /vault/a/b/c.md", arguments: BridgeFixtures.Whitelisted);

        _vault.Files["a/b/c.md"].ShouldBe("x\n");
        Changes(result).ShouldBe([("/vault/a/b/c.md", VfsChange.Operations.Create, VfsChange.Statuses.Applied)]);
    }

    // Bytes that are not text go the way a copy would put them there.
    [Fact]
    public async Task BinaryContent_IsABlobWrite()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01];

        var result = await Tool((call, ct) => call.WriteAsync("/vault/pic.png", png, isNew: true, ct))
            .RunAsync("/sandbox", "cp pic.png /vault/", arguments: BridgeFixtures.Whitelisted);

        _vault.Writes.ShouldBe(["blob pic.png"]);
        Changes(result).ShouldBe([("/vault/pic.png", VfsChange.Operations.Create, VfsChange.Statuses.Applied)]);
    }

    // The text tool refuses an extension the mount does not author as text; the shell is refused
    // exactly the same way, with the mount's own envelope.
    [Fact]
    public async Task AWriteTheToolWouldRefuse_IsRefusedTheSameWay()
    {
        BridgeAnswer<bool>? answer = null;
        var result = await Tool(async (call, ct) => answer = await call.WriteAsync("/vault/run.exe", "MZ"u8.ToArray(), isNew: true, ct))
            .RunAsync("/sandbox", "echo MZ > /vault/run.exe", arguments: BridgeFixtures.Whitelisted);

        answer.ShouldBeOfType<BridgeAnswer<bool>.Refused>().Errno.ShouldBe(Errnos.Invalid);
        _vault.Files.ShouldNotContainKey("run.exe");
        var change = result["vfsChanges"]!.AsArray().ShouldHaveSingleItem()!;
        change["status"]!.GetValue<string>().ShouldBe(VfsChange.Statuses.Refused);
        change["error"]!["message"]!.GetValue<string>().ShouldContain("Extension not allowed");
    }

    // A rendered status file refuses writes as the tool's call would be refused.
    [Fact]
    public async Task AWriteToAMountThatRefusesIt_IsListedRefusedWithTheMountsEnvelope()
    {
        var result = await Tool((call, ct) => call.WriteAsync("/timers/eggs/status.json", "{}"u8.ToArray(), isNew: false, ct))
            .RunAsync("/sandbox", "sed -i s/120/0/ /timers/eggs/status.json", arguments: BridgeFixtures.Whitelisted);

        var change = result["vfsChanges"]!.AsArray().ShouldHaveSingleItem()!;
        change["status"]!.GetValue<string>().ShouldBe(VfsChange.Statuses.Refused);
        change["error"]!["errorCode"]!.GetValue<string>().ShouldBe("unsupported_operation");
    }

    // The text tool would ask the person: the shell is refused, listed, and nobody is asked.
    [Fact]
    public async Task AWriteTheToolWouldAskAbout_IsRefusedAndListed()
    {
        BridgeAnswer<bool>? answer = null;
        var result = await Tool(async (call, ct) => answer = await call.WriteAsync("/vault/notes/todo.md", "x"u8.ToArray(), isNew: false, ct))
            .RunAsync("/sandbox", "echo x > /vault/notes/todo.md",
                arguments: BridgeFixtures.Allowing(FileSystemToolFeature.Callable(VfsFileReadTool.Name)));

        answer.ShouldBeOfType<BridgeAnswer<bool>.Refused>().Errno.ShouldBe(Errnos.Denied);
        _vault.Files["notes/todo.md"].ShouldBe("old\n");
        Changes(result).ShouldBe([("/vault/notes/todo.md", VfsChange.Operations.Write, VfsChange.Statuses.Refused)]);
    }

    // What a command wrote is what the next read in the same command sees.
    [Fact]
    public async Task AWriteThenARead_SeesTheNewContent()
    {
        byte[]? read = null;
        await Tool(async (call, ct) =>
        {
            await call.WriteAsync("/vault/notes/todo.md", "fresh\n"u8.ToArray(), isNew: false, ct);
            read = (await call.ReadAsync("/vault/notes/todo.md", ct)).ShouldBeOfType<BridgeAnswer<byte[]>.Ok>().Value;
        }).RunAsync("/sandbox", "echo fresh > f; cat f", arguments: BridgeFixtures.Whitelisted);

        Encoding.UTF8.GetString(read!).ShouldBe("fresh\n");
    }
}