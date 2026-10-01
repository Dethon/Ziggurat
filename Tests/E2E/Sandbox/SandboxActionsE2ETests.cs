using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.E2E.Sandbox;

// Action files run from any script, in the real image: the kernel executes the helper the daemon
// serves for every action file (mode 0111, never readable), the helper asks the call's daemon, and
// the action's own output and exit code come back as the helper's.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxActionsE2ETests(SandboxE2EFixture fixture)
{
    private readonly JobsMount _jobs = new();

    private VfsCall Mint() => fixture.Bridge.Mint(BridgeFixtures.Registry((_jobs, "/jobs", null)), _ => true);

    [SkippableFact]
    public async Task AnActionRunFromBash_PassesItsOutputAndExitCodeThrough()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "/jobs/run --fast 'two words'; cd /jobs && ./fail; echo exit=$?", call, cts.Token);

        Stdout(result).ShouldBe("ran with --fast|two words\nexit=3\n", result.ToString());
        result.GetProperty("stderr").GetString().ShouldBe("failed on purpose\n");
        call.Changes.Select(c => (c.Path, c.Operation, c.Status)).ShouldBe([
            ("/jobs/run", VfsChange.Operations.Action, VfsChange.Statuses.Applied),
            ("/jobs/fail", VfsChange.Operations.Action, VfsChange.Statuses.Applied)
        ]);
    }

    [SkippableFact]
    public async Task AnActionCalledThroughPythonsSubprocess_BehavesTheSame()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client,
            "python3 -c \"import subprocess; r = subprocess.run(['/jobs/run', 'x'], capture_output=True, text=True); print(r.returncode, r.stdout.strip())\"",
            Mint(), cts.Token);

        Stdout(result).ShouldBe("0 ran with x\n", result.ToString());
    }

    // Executable-only: it runs, and nobody reads it.
    [SkippableFact]
    public async Task CatOfAnActionFile_IsRefused()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client, "cat /jobs/run; stat -c %a /jobs/run", Mint(), cts.Token);

        result.GetProperty("stderr").GetString()!.ShouldContain("Permission denied");
        Stdout(result).ShouldBe("111\n", result.ToString());
    }

    // A file the script made a moment ago is on the mount by the time the action runs.
    [SkippableFact]
    public async Task AFileCreatedEarlierInTheScript_IsVisibleToTheAction()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client, "echo prepared > /jobs/input.txt && /jobs/list", Mint(), cts.Token);

        Stdout(result).ShouldBe("input.txt\n", result.ToString());
    }

    // The helper is only ever an action file under /vfs, run inside an exec that has the mounts.
    [SkippableFact]
    public async Task TheHelperRunOutsideAnActionFile_IsRefused()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await ExecAsync(client, "/usr/local/bin/vfs-action; echo exit=$?", Mint(), cts.Token);

        Stdout(result).ShouldBe("exit=126\n", result.ToString());
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

    // A mount with three actions and room for one file, the shape of the timers' catalog.
    private sealed class JobsMount : FileSystemBackendBase
    {
        private static readonly string[] _actions = ["run", "fail", "list"];
        private readonly Dictionary<string, string> _files = new();

        public override string FilesystemName => "jobs";

        public override string DescribeMount => "Jobs.";

        public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
            Task.FromResult<FsResult<FsInfoResult>>(new FsResult<FsInfoResult>.Ok(path.Trim('/') switch
            {
                "" => new FsInfoResult { Exists = true, Path = path, IsDirectory = true },
                var name when _actions.Contains(name) => new FsInfoResult { Exists = true, Path = path, IsDirectory = false, Executable = true },
                var name when _files.TryGetValue(name, out var text) => new FsInfoResult { Exists = true, Path = path, IsDirectory = false, Size = text.Length },
                _ => new FsInfoResult { Exists = false, Path = path }
            }));

        public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
            Task.FromResult<FsResult<FsGlobResult>>(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = [.. _actions, .. _files.Keys],
                Executables = _actions,
                Truncated = false,
                Total = _actions.Length + _files.Count
            }));

        public override Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
        {
            _files[path.Trim('/')] = content;
            return Task.FromResult<FsResult<FsCreateResult>>(new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created", FilePath = path, Size = $"{content.Length}B", Lines = 1
            }));
        }

        public override Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
        {
            // The words as the mount's tokenizer reads them back, joined so a test can see each one.
            var words = System.Text.RegularExpressions.Regex.Matches(command, @"'([^']*)'|(\S+)")
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                .ToList();
            var args = string.Join("|", words.Skip(1));
            var (code, stdout, stderr) = words[0] switch
            {
                "./run" => (0, $"ran with {args}\n", ""),
                "./fail" => (3, "", "failed on purpose\n"),
                "./list" => (0, string.Join("\n", _files.Keys) + "\n", ""),
                _ => (127, "", $"command not found: {words[0]}\n")
            };
            return Task.FromResult<FsResult<FsExecResult>>(new FsResult<FsExecResult>.Ok(
                BridgeFixtures.Ran(path, code) with { Stdout = stdout, Stderr = stderr }));
        }
    }
}