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

// Every mount of the calling session is a filesystem to the shell: the real image, its launcher
// and its FUSE daemon, against the agent's bridge endpoint hosted by the fixture over an in-memory
// registry. A test mints a call the way the exec tool does and hands its token over on `_meta`.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxMountsE2ETests(SandboxE2EFixture fixture)
{
    private static (MemoryDisk Vault, RenderedMount Timers) Mounts() => (
        new MemoryDisk("vault", new Dictionary<string, string>
        {
            ["notes/todo.md"] = "- [ ] TODO buy milk\n",
            ["notes/deep/more.md"] = "another TODO here\n",
            ["inbox.md"] = "nothing to see\n"
        }),
        new RenderedMount("timers", new Dictionary<string, string>
        {
            ["eggs/status.json"] = """{"label":"eggs","remainingSeconds":120}""",
            ["tea/status.json"] = """{"label":"tea","remainingSeconds":30}"""
        }));

    private VfsCall Mint(params (global::Domain.Contracts.FileSystemBackendBase Backend, string MountPoint, ShellReach? Reach)[] extra)
    {
        var (vault, timers) = Mounts();
        var registry = BridgeFixtures.Registry(
            [(vault, "/vault", null), (timers, "/timers", null), .. extra]);
        return fixture.Bridge.Mint(registry, BridgeFixtures.Everything, null, null);
    }

    [SkippableFact]
    public async Task GrepOverTheVault_FindsWhatTheSearchToolWouldFind()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "grep -rl TODO /vault | sort; cat /vault/notes/todo.md", call, cts.Token);

        Stdout(result).ShouldBe("/vault/notes/deep/more.md\n/vault/notes/todo.md\n- [ ] TODO buy milk\n", result.ToString());
    }

    // The daemon answers every file operation and action a command makes, so it is the root
    // process a command can reach most of; it mounts as root and then keeps nothing, in every
    // thread — the bounding set and no-new-privs are per thread, and a thread that kept them could
    // still exec a setuid-root binary back to root. The unit unmounts once it is done.
    [SkippableFact]
    public async Task TheDaemonServingACommand_HoldsNoPrivilegeOnceMounted()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client, """
            cat /vault/inbox.md >/dev/null
            pid=$(pgrep -x vfs-daemon | head -1)
            [ -n "$pid" ] || { echo "no daemon"; exit 2; }
            for task in /proc/$pid/task/*; do grep -E '^(Uid|CapEff|CapBnd|NoNewPrivs)' $task/status; done
            """, Mint(), cts.Token);

        var stdout = Stdout(result);
        stdout.ShouldNotContain("no daemon");
        stdout.ShouldContain("Uid:");
        stdout.ShouldNotMatch(@"Uid:\s+0\s");
        stdout.ShouldNotMatch(@"CapEff:\s+0*[1-9a-f]");
        stdout.ShouldNotMatch(@"CapBnd:\s+0*[1-9a-f]");
        stdout.ShouldNotMatch(@"NoNewPrivs:\s+0");
    }

    // A mount is a link into /vfs, and find does not follow a link it starts from unless asked; a
    // login shell asks for it, so `find /vault` walks the vault as it would a directory.
    [SkippableFact]
    public async Task FindFromAMountsPath_WalksTheMount()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "find /vault -name '*.md' | sort", call, cts.Token);

        Stdout(result).ShouldBe(
            "/vault/inbox.md\n/vault/notes/deep/more.md\n/vault/notes/todo.md\n", result.ToString());
    }

    // A rendered file has no size the mount can give; served with direct I/O it is read in full.
    [SkippableFact]
    public async Task JqOnARenderedStatusFile_ReadsItWhole()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client,
            "jq -r .remainingSeconds /timers/eggs/status.json; wc -c < /timers/tea/status.json; python3 -c \"print(open('/timers/tea/status.json').read())\"",
            call, cts.Token);

        Stdout(result).ShouldBe("120\n37\n{\"label\":\"tea\",\"remainingSeconds\":30}\n", result.ToString());
    }

    [SkippableFact]
    public async Task LsOfARenderedDirectory_ListsWhatGlobLists()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "ls /timers; ls -d /timers/*/", call, cts.Token);

        Stdout(result).ShouldBe("eggs\ntea\n/timers/eggs/\n/timers/tea/\n", result.ToString());
    }

    // An outpost is somebody's own computer, and the sandbox's own disk is the shell's already.
    [SkippableFact]
    public async Task NeitherAnOutpostNorTheSandbox_IsServed()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint(
            (new MemoryDisk("laptop", new Dictionary<string, string> { ["secret.txt"] = "mine" }), "outpost:laptop", null),
            (new MemoryDisk("sandbox", new Dictionary<string, string> { ["x"] = "y" }), "/sandbox", ShellReach.Contained));

        var result = await ExecAsync(client, "ls /vfs; ls /laptop 2>&1 | head -1", call, cts.Token);

        var stdout = Stdout(result);
        stdout.ShouldStartWith("timers\nvault\n", customMessage: result.ToString());
        stdout.ShouldContain("No such file or directory");
    }

    // A name the image already uses keeps the image's directory; the mount is still reachable at
    // /vfs/<name>.
    [SkippableFact]
    public async Task AMountNamedLikeAnImageDirectory_IsServedOnlyUnderVfs()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint((new MemoryDisk("etc", new Dictionary<string, string> { ["mine.txt"] = "served\n" }), "/etc", null));

        var result = await ExecAsync(client, "cat /vfs/etc/mine.txt; test -f /etc/os-release && echo image", call, cts.Token);

        Stdout(result).ShouldBe("served\nimage\n", result.ToString());
    }

    // The media library's mount is `media`, and the image's empty /media is gone so it can link.
    [SkippableFact]
    public async Task AMountNamedMedia_IsLinkedAtSlashMedia()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint((new MemoryDisk("media", new Dictionary<string, string> { ["film.txt"] = "reel\n" }), "/media", null));

        var result = await ExecAsync(client, "cat /media/film.txt", call, cts.Token);

        Stdout(result).ShouldBe("reel\n", result.ToString());
    }

    // The token is the daemon's alone: it reaches the launcher over its socket and the daemon over
    // a pipe, never the command's environment, argv or a file the command can read.
    [SkippableFact]
    public async Task TheToken_NeverReachesAnythingTheCommandCanRead()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        // Searched for hex-encoded, so the command's own command line — which /proc also shows —
        // never holds the token it is looking for.
        var hex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(call.Token)).ToLowerInvariant();

        var result = await ExecAsync(client, $"""
            ls /vault >/dev/null
            env | od -An -tx1 | tr -d ' \n' | grep -c {hex}
            cat /proc/*/cmdline /proc/*/environ 2>/dev/null | od -An -tx1 | tr -d ' \n' | grep -c {hex}
            find /run /tmp /home -type f -readable -print0 2>/dev/null | xargs -0 cat 2>/dev/null | od -An -tx1 | tr -d ' \n' | grep -c {hex}
            """, call, cts.Token);

        Stdout(result).ShouldBe("0\n0\n0\n", result.ToString());
    }

    // Each exec has its own namespace and its own daemon: two sessions running at once each see
    // their own mounts.
    [SkippableFact]
    public async Task ConcurrentExecsFromTwoSessions_EachSeeTheirOwnMounts()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var first = fixture.Bridge.Mint(BridgeFixtures.Registry(
            (new MemoryDisk("vault", new Dictionary<string, string> { ["who.txt"] = "first\n" }), "/vault", null)), BridgeFixtures.Everything, null, null);
        var second = fixture.Bridge.Mint(BridgeFixtures.Registry(
            (new MemoryDisk("vault", new Dictionary<string, string> { ["who.txt"] = "second\n" }), "/vault", null)), BridgeFixtures.Everything, null, null);

        var results = await Task.WhenAll(
            ExecAsync(client, "sleep 1; cat /vault/who.txt", first, cts.Token),
            ExecAsync(client, "sleep 1; cat /vault/who.txt", second, cts.Token));

        Stdout(results[0]).ShouldBe("first\n", results[0].ToString());
        Stdout(results[1]).ShouldBe("second\n", results[1].ToString());
    }

    // One container and one uid: a command of another session can see this one's processes, and
    // /proc/<pid>/root is a way into this exec's namespace, where its mounts are. The mount
    // namespace alone does not keep them apart, so the daemon answers only its own exec's processes.
    [SkippableFact]
    public async Task ACommandOfAnotherSession_CannotReadThisOnesMountsThroughItsProcesses()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var owner = fixture.Bridge.Mint(BridgeFixtures.Registry(
            (new MemoryDisk("vault", new Dictionary<string, string> { ["who.txt"] = "the owner's\n" }), "/vault", null)), BridgeFixtures.Everything, null, null);
        var stranger = fixture.Bridge.Mint(BridgeFixtures.Registry(
            (new MemoryDisk("scratch", new Dictionary<string, string> { ["x.txt"] = "x\n" }), "/scratch", null)), BridgeFixtures.Everything, null, null);
        // The bracket keeps the stranger's own command line from matching itself.
        const string Reach = "sleep 1; for p in /proc/[0-9]*; do "
                             + "if grep -qa '4\\.3133[7]' $p/cmdline 2>/dev/null; then cat $p/root/vfs/vault/who.txt; ls $p/root/vfs/vault; fi; "
                             + "done 2>&1; echo looked";

        var results = await Task.WhenAll(
            ExecAsync(client, "cat /vault/who.txt; sleep 4.31337", owner, cts.Token),
            ExecAsync(client, Reach, stranger, cts.Token));

        Stdout(results[0]).ShouldBe("the owner's\n", results[0].ToString());
        Stdout(results[1]).ShouldEndWith("looked\n", Case.Sensitive, results[1].ToString());
        Stdout(results[1]).ShouldNotContain("the owner's", Case.Sensitive, results[1].ToString());
        Stdout(results[1]).Split('\n').ShouldNotContain("who.txt", results[1].ToString());
        Stdout(results[1]).ShouldContain("Permission denied", Case.Sensitive, results[1].ToString());
    }

    // The cache is the call's: what the file tools change between two execs, the second one sees.
    [SkippableFact]
    public async Task TheNextExec_SeesWhatTheToolsChangedBetweenThem()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var (vault, _) = Mounts();
        var registry = BridgeFixtures.Registry((vault, "/vault", null));

        var first = await ExecAsync(client, "cat /vault/inbox.md", fixture.Bridge.Mint(registry, BridgeFixtures.Everything, null, null), cts.Token);
        vault.Files["inbox.md"] = "changed by a tool\n";
        var second = await ExecAsync(client, "cat /vault/inbox.md", fixture.Bridge.Mint(registry, BridgeFixtures.Everything, null, null), cts.Token);

        Stdout(first).ShouldBe("nothing to see\n");
        Stdout(second).ShouldBe("changed by a tool\n");
    }

    // A vault-sized tree stays practical to search from the shell.
    [SkippableFact]
    public async Task ARecursiveGrepOverAVaultSizedTree_StaysPractical()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var files = Enumerable.Range(0, 40)
            .SelectMany(d => Enumerable.Range(0, 25).Select(f => (Path: $"d{d}/n{f}.md", Text: f == 0 ? "TODO here\n" : "nothing\n")))
            .ToDictionary(x => x.Path, x => x.Text);
        var call = fixture.Bridge.Mint(BridgeFixtures.Registry((new MemoryDisk("vault", files), "/vault", null)), BridgeFixtures.Everything, null, null);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var result = await ExecAsync(client, "grep -rl TODO /vault | wc -l", call, cts.Token);

        Stdout(result).ShouldBe("40\n", result.ToString());
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    // An exec the agent minted nothing for serves nothing.
    [SkippableFact]
    public async Task AnExecWithNoToken_SeesNoMounts()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client, "ls -A /vfs | wc -l", null, cts.Token);

        Stdout(result).ShouldBe("0\n", result.ToString());
    }

    private static string Stdout(JsonElement result) =>
        result.TryGetProperty("stdout", out var stdout) ? stdout.GetString()! : result.ToString();

    private static async Task<JsonElement> ExecAsync(McpClient client, string command, VfsCall? call, CancellationToken ct)
    {
        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "fs_exec",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(""),
                ["command"] = JsonSerializer.SerializeToElement(command)
            },
            Meta = call is null ? null : new JsonObject { [VfsBridgeGrant.MetaKey] = new VfsBridgeGrant(call.Token).ToMeta() }
        }, cancellationToken: ct);

        return JsonDocument.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))
            .RootElement.Clone();
    }
}