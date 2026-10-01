using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sandbox;

// What a command can and cannot do, asked of the real image: the root launcher starts the server
// as a uid of its own and runs every command as PUID, with no capabilities, no way to gain any and
// none of the server's environment. Nothing below the image can say this — the in-process runner
// is the test user running bash.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxLauncherE2ETests(SandboxE2EFixture fixture)
{
    [SkippableFact]
    public async Task ACommand_RunsAsPuidWithNoCapabilities()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client,
            "id -u; id -g; grep -E '^(CapEff|CapBnd|NoNewPrivs)' /proc/self/status", cts.Token);

        var stdout = Stdout(result);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe(SandboxE2EFixture.Uid.ToString(), stdout);
        lines[1].ShouldBe(SandboxE2EFixture.Gid.ToString(), stdout);
        stdout.ShouldContain("CapEff:\t0000000000000000");
        stdout.ShouldContain("CapBnd:\t0000000000000000");
        stdout.ShouldContain("NoNewPrivs:\t1");
    }

    [SkippableFact]
    public async Task ACommand_SeesNoneOfTheServersEnvironment()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client, "env; echo HOME=$HOME", cts.Token);

        var stdout = Stdout(result);
        stdout.ShouldNotContain(SandboxE2EFixture.PlantedSecret);
        stdout.ShouldNotContain(SandboxE2EFixture.PlantedSecretName);
        stdout.ShouldNotContain(McpTestSecret.Value);
        stdout.ShouldContain($"HOME={SandboxE2EFixture.ContainerWorkspace}");
    }

    // The server holds the deployment secret; a command may not read it out of /proc, attach to
    // it, or stop it.
    [SkippableFact]
    public async Task ACommand_CannotReadTraceOrKillTheServer()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client, """
            pid=$(pgrep -f McpServerSandbox.dll | head -1)
            [ -n "$pid" ] || { echo "no server"; exit 2; }
            cat /proc/$pid/environ >/dev/null 2>&1 && echo "environ readable"
            kill -0 $pid 2>/dev/null && echo "killable"
            echo done
            """, cts.Token);

        var stdout = Stdout(result);
        stdout.ShouldNotContain("no server");
        stdout.ShouldNotContain("environ readable");
        stdout.ShouldNotContain("killable");
        stdout.ShouldContain("done");
    }

    // The container holds SYS_ADMIN for FUSE; a command must not get it back, neither by asking
    // for a namespace of its own nor through the one setuid binary that could mount. A user
    // namespace needs no capability to make — Docker's filter allows it once the container holds
    // SYS_ADMIN — and is root inside, which is where most kernel escalations start; on master's
    // container it was refused, and the launcher's own filter keeps it so on every host.
    [SkippableFact]
    public async Task ACommand_CannotUnshareOrMount()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client, """
            unshare -m true 2>/dev/null && echo "unshared"
            unshare -Ur true 2>/dev/null && echo "user namespace"
            unshare -Urm sh -c 'mount -t tmpfs none /tmp' 2>/dev/null && echo "mounted in a user namespace"
            [ -u /usr/bin/fusermount3 ] && echo "setuid fusermount3"
            mkdir -p /tmp/m && fusermount3 -o ro /tmp/m 2>/dev/null && echo "mounted"
            echo done
            """, cts.Token);

        var stdout = Stdout(result);
        stdout.ShouldNotContain("unshared");
        stdout.ShouldNotContain("user namespace");
        stdout.ShouldNotContain("setuid fusermount3");
        stdout.ShouldNotContain("mounted");
        stdout.ShouldContain("done");
    }

    // The container holds SYS_ADMIN, which Docker's own filter answers with bpf, perf_event_open,
    // setns and more — enough to read kernel memory and write into the host's processes. Only the
    // launcher and the vfs daemon hold it, so this is what a command would have if it ever ran as
    // one of them: the namespace the unit makes and nothing else. Each syscall is called with
    // nonsense, so an allowed one fails EINVAL or EFAULT and only the filter answers EPERM.
    [SkippableFact]
    public async Task ARootProcessInTheContainer_GetsOnlyTheLaunchersSyscalls()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var output = await fixture.ExecAsRootAsync("""
            python3 -c "
            import ctypes, os
            libc = ctypes.CDLL(None, use_errno=True)
            for name, nr in (('bpf', 321), ('perf_event_open', 298), ('setns', 308)):
                libc.syscall(nr, 0, 0, 0, 0, 0)
                print(name, os.strerror(ctypes.get_errno()))
            "
            unshare -U true 2>/dev/null && echo "user namespace"
            unshare -m true && echo "mount namespace"
            """, cts.Token);

        output.ShouldContain("bpf Operation not permitted");
        output.ShouldContain("perf_event_open Operation not permitted");
        output.ShouldContain("setns Operation not permitted");
        output.ShouldNotContain("user namespace");
        output.ShouldContain("mount namespace");
    }

    // The filter answers clone3 ENOSYS, because its flags sit behind a pointer a filter cannot
    // read; glibc then falls back to clone, whose flags it can. Threads and process spawning are
    // what that fallback carries, so both must still work.
    [SkippableFact]
    public async Task ACommand_StillMakesThreadsAndProcesses()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client, """
            python3 -c "
            import subprocess, threading
            out = []
            t = threading.Thread(target=lambda: out.append(subprocess.run(['echo', 'spawned'], capture_output=True, text=True).stdout.strip()))
            t.start(); t.join()
            print('thread', out[0])
            "
            """, cts.Token);

        Stdout(result).ShouldContain("thread spawned");
    }

    [SkippableFact]
    public async Task ATimeout_KillsTheWholeTree()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var timedOut = await ExecAsync(client, "(sleep 377 &) ; sleep 378", cts.Token, timeoutSeconds: 2);
        var after = await ExecAsync(client, "pgrep -f 'sleep 37[78]' || echo none", cts.Token);

        timedOut.GetProperty("timedOut").GetBoolean().ShouldBeTrue(timedOut.ToString());
        timedOut.GetProperty("exitCode").GetInt32().ShouldBe(-1);
        Stdout(after).Trim().ShouldBe("none");
    }

    [SkippableFact]
    public async Task ExitCodesAndTheOutputCap_AreAsBefore()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var failed = await ExecAsync(client, "echo out; echo err >&2; exit 7", cts.Token);
        var flood = await ExecAsync(client, "head -c 200000 /dev/zero | tr '\\0' 'x'", cts.Token);

        failed.GetProperty("exitCode").GetInt32().ShouldBe(7);
        Stdout(failed).ShouldBe("out\n");
        failed.GetProperty("stderr").GetString().ShouldBe("err\n");
        flood.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        Stdout(flood).Length.ShouldBe(65536);
    }

    // The file tools run inside the server, as its own uid; commands run as PUID. Both have to be
    // able to change what the other made, or a note the agent wrote is one a script cannot edit.
    [SkippableFact]
    public async Task AFileTheToolWrote_ACommandCanEditAndMakeExecutable()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var name = $"tool-{Guid.NewGuid():N}.sh";

        var created = await client.CallToolAsync("fs_create", new Dictionary<string, object?>
        {
            ["path"] = $"home/sandbox_user/{name}",
            ["content"] = "echo before\n",
            ["overwrite"] = false,
            ["createDirectories"] = true
        }, cancellationToken: cts.Token);
        var edited = await ExecAsync(client,
            $"cd ~ && sed -i 's/before/after/' {name} && chmod +x {name} && ./{name}", cts.Token);

        Parse(created).TryGetProperty("ok", out var ok).ShouldBeFalse(Parse(created).ToString());
        edited.GetProperty("exitCode").GetInt32().ShouldBe(0, edited.ToString());
        Stdout(edited).ShouldBe("after\n");
    }

    [SkippableFact]
    public async Task AFileACommandWrote_TheToolCanEdit()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var name = $"command-{Guid.NewGuid():N}.md";

        await ExecAsync(client, $"mkdir -p ~/made && echo before > ~/made/{name}", cts.Token);
        var edited = await client.CallToolAsync("fs_edit", new Dictionary<string, object?>
        {
            ["path"] = $"home/sandbox_user/made/{name}",
            ["edits"] = new[] { new Dictionary<string, object?> { ["oldString"] = "before", ["newString"] = "after" } }
        }, cancellationToken: cts.Token);
        var read = await ExecAsync(client, $"cat ~/made/{name}", cts.Token);

        Parse(edited).TryGetProperty("ok", out var ok).ShouldBeFalse(Parse(edited).ToString());
        Stdout(read).ShouldBe("after\n");
    }

    private static string Stdout(JsonElement result) =>
        result.TryGetProperty("stdout", out var stdout) ? stdout.GetString()! : result.ToString();

    private static async Task<JsonElement> ExecAsync(
        McpClient client, string command, CancellationToken ct, int? timeoutSeconds = null)
    {
        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = "",
            ["command"] = command,
            ["timeoutSeconds"] = timeoutSeconds
        }, cancellationToken: ct);

        return Parse(result);
    }

    private static JsonElement Parse(CallToolResult result) =>
        JsonDocument.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))
            .RootElement.Clone();
}