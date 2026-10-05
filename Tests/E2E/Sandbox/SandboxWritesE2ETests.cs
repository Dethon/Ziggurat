using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.E2E.Sandbox;

// The shell writing files on mounts, in the real image: what reaches the mount is what the kernel's
// operations amount to once the daemon has decided when they commit. Bash swallows a refusal at
// close, so the bridge's change log — what the exec result carries — is the record asserted.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxWritesE2ETests(SandboxE2EFixture fixture)
{
    private readonly MemoryDisk _vault = new("vault", new Dictionary<string, string>
    {
        ["notes/todo.md"] = "- [ ] buy milk\n"
    });

    private readonly RenderedMount _timers = new("timers", new Dictionary<string, string>
    {
        ["eggs/status.json"] = """{"remainingSeconds":120}"""
    });

    private VfsCall Mint() => fixture.Bridge.Mint(
        BridgeFixtures.Registry((_vault, "/vault", null), (_timers, "/timers", null)), BridgeFixtures.Everything, null, null);

    private static IReadOnlyList<(string, string, string)> Changes(VfsCall call) =>
        [.. call.Changes.Select(c => (c.Path, c.Operation, c.Status))];

    // No empty intermediate: bash's open-truncate and its double flush never reach the mount.
    [SkippableFact]
    public async Task EchoIntoANewNote_CreatesItOnce()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "echo text > /vault/new.md && cat /vault/new.md", call, cts.Token);

        Stdout(result).ShouldBe("text\n", result.ToString());
        _vault.Files["new.md"].ShouldBe("text\n");
        _vault.Writes.ShouldBe(["create new.md"]);
        Changes(call).ShouldBe([("/vault/new.md", "create", "applied")]);
    }

    [SkippableFact]
    public async Task EchoOverAnExistingNote_WritesItOnce()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        await ExecAsync(client, "echo replaced > /vault/notes/todo.md; echo more >> /vault/notes/todo.md", call, cts.Token);

        _vault.Files["notes/todo.md"].ShouldBe("replaced\nmore\n");
        Changes(call).ShouldBe([("/vault/notes/todo.md", "write", "applied"), ("/vault/notes/todo.md", "write", "applied")]);
    }

    // `sed -i` writes a temp file beside the note and renames it over: one write to the note, and
    // the temp never appears on the mount.
    [SkippableFact]
    public async Task SedInPlace_CommitsOneWriteToTheNote()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "sed -i 's/buy/bought/' /vault/notes/todo.md && cat /vault/notes/todo.md", call, cts.Token);

        Stdout(result).ShouldBe("- [ ] bought milk\n", result.ToString());
        _vault.Writes.ShouldBe(["create notes/todo.md"]);
        _vault.Files.Keys.ShouldBe(["notes/todo.md"]);
        Changes(call).ShouldBe([("/vault/notes/todo.md", "write", "applied")]);
    }

    // The spike's `sed -i` replaced a refusing file through its rename; judged as a write to the
    // target, it is refused, the file is unchanged, and the refusal is listed with the mount's words.
    [SkippableFact]
    public async Task SedInPlaceOnAFileTheMountRefuses_LeavesItAndListsTheRefusal()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "sed -i 's/120/0/' /timers/eggs/status.json; cat /timers/eggs/status.json", call, cts.Token);

        Stdout(result).ShouldBe("""{"remainingSeconds":120}""", result.ToString());
        var change = call.Changes.ShouldHaveSingleItem();
        (change.Path, change.Operation, change.Status).ShouldBe(("/timers/eggs/status.json", "write", "refused"));
        change.Error!["errorCode"]!.GetValue<string>().ShouldBe("unsupported_operation");
    }

    [SkippableFact]
    public async Task MkdirThenAFile_CreatesTheNoteWithItsDirectories_AndAnEmptyMkdirLeavesNothing()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        await ExecAsync(client, "mkdir -p /vault/a/b && echo x > /vault/a/b/c.md && mkdir /vault/empty", call, cts.Token);

        _vault.Files["a/b/c.md"].ShouldBe("x\n");
        _vault.Writes.ShouldBe(["create a/b/c.md"]);
        Changes(call).ShouldBe([("/vault/a/b/c.md", "create", "applied")]);
    }

    // The text tool refuses an extension the vault does not author as text; so does the shell.
    [SkippableFact]
    public async Task AWriteTheToolWouldRefuse_IsRefusedTheSameWay()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "echo MZ > /vault/run.exe; echo exit=$?", call, cts.Token);

        // Bash swallows the refusal at close — which is why the change list exists.
        Stdout(result).ShouldBe("exit=0\n", result.ToString());
        _vault.Files.ShouldNotContainKey("run.exe");
        Changes(call).ShouldBe([("/vault/run.exe", "create", "refused")]);
    }

    private static string Stdout(JsonElement result) =>
        result.TryGetProperty("stdout", out var stdout) ? stdout.GetString()! : result.ToString();

    private static async Task<JsonElement> ExecAsync(McpClient client, string command, VfsCall call, CancellationToken ct)
    {
        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "fs_exec",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(""),
                ["command"] = JsonSerializer.SerializeToElement(command)
            },
            Meta = new JsonObject { [VfsBridgeGrant.MetaKey] = new VfsBridgeGrant(call.Token).ToMeta() }
        }, cancellationToken: ct);

        return JsonDocument.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))
            .RootElement.Clone();
    }
}