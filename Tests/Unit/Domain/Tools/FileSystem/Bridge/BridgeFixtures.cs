using System.Runtime.CompilerServices;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// The sandbox as the exec tool sees it, with the container replaced by a script: instead of running
// a command, it replays the FUSE operations a command would have caused, against the bridge, with
// the token the tool minted. What it returns is the exec result the launcher would have answered.
internal sealed class ScriptedSandbox(VfsBridge bridge, Func<VfsCall, CancellationToken, Task<FsExecResult>> script)
    : FileSystemBackendBase, IBridgedExecBackend
{
    public const string Mount = "/sandbox";

    public List<string> TokensSeen { get; } = [];

    public override string FilesystemName => "sandbox";

    public override string DescribeMount => "Scripted sandbox.";

    public override ShellReach? ShellReach => global::Domain.DTOs.ShellReach.Contained;

    public override Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        Task.FromResult<FsResult<FsExecResult>>(new FsResult<FsExecResult>.Ok(BridgeFixtures.Ran(path)));

    public async Task<FsResult<FsExecResult>> ExecAsync(
        string path, string command, int? timeoutSeconds, VfsBridgeGrant grant, CancellationToken ct)
    {
        TokensSeen.Add(grant.Token);
        var call = bridge.Find(grant.Token) ?? throw new InvalidOperationException("the tool handed over a token the bridge does not know");
        return new FsResult<FsExecResult>.Ok(await script(call, ct));
    }
}

// A disk root in memory: bytes behind every file, info with a size, a glob over one level.
internal class MemoryDisk(string name, IDictionary<string, string> files) : FileSystemBackendBase
{
    public IDictionary<string, string> Files { get; } = files;

    public override string FilesystemName => name;

    public override string DescribeMount => $"In-memory {name}.";

    public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var key = path.Trim('/');
        var isDir = Files.Keys.Any(k => k.StartsWith(key + "/", StringComparison.Ordinal));
        return Task.FromResult<FsResult<FsInfoResult>>(new FsResult<FsInfoResult>.Ok(
            Files.TryGetValue(key, out var content)
                ? new FsInfoResult { Exists = true, Path = path, IsDirectory = false, Size = Encoding.UTF8.GetByteCount(content) }
                : new FsInfoResult { Exists = isDir, Path = path, IsDirectory = isDir ? true : null }));
    }

    public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var prefix = basePath.Trim('/') is { Length: > 0 } b ? b + "/" : "";
        var entries = Files.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..])
            .Select(rest => rest.Contains('/') ? prefix + rest[..rest.IndexOf('/')] + "/" : prefix + rest)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(Glob(entries));
    }

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        if (!Files.TryGetValue(path.Trim('/'), out var content))
        {
            throw new FileSystemOperationException(new global::Domain.Tools.ToolErrorResult
            {
                ErrorCode = global::Domain.Tools.ToolError.Codes.NotFound,
                Message = $"Path not found: {path}"
            });
        }

        yield return Encoding.UTF8.GetBytes(content);
    }
}

// A mount with no bytes behind it: the text is rendered on each read and info cannot say how long
// it will be.
internal sealed class RenderedMount(string name, IDictionary<string, string> files) : FileSystemBackendBase
{
    public override string FilesystemName => name;

    public override string DescribeMount => $"Rendered {name}.";

    public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        Task.FromResult<FsResult<FsInfoResult>>(new FsResult<FsInfoResult>.Ok(
            files.ContainsKey(path.Trim('/'))
                ? new FsInfoResult { Exists = true, Path = path, IsDirectory = false }
                : new FsInfoResult { Exists = files.Keys.Any(k => k.StartsWith(path.Trim('/') + "/")), Path = path, IsDirectory = true }));

    public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        Task.FromResult<FsResult<FsReadResult>>(files.TryGetValue(path.Trim('/'), out var content)
            ? new FsResult<FsReadResult>.Ok(new FsReadResult
            {
                FilePath = path, Content = content, TotalLines = content.Split('\n').Length, Truncated = false
            })
            : FsError.NotFound<FsReadResult>(path));

    public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var prefix = basePath.Trim('/') is { Length: > 0 } b ? b + "/" : "";
        return Task.FromResult(Glob(files.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..])
            .Select(rest => rest.Contains('/') ? prefix + rest[..rest.IndexOf('/')] + "/" : prefix + rest)
            .Distinct()
            .ToList()));
    }
}

internal static class BridgeFixtures
{
    public static FsExecResult Ran(string cwd, int exitCode = 0) => new()
    {
        Stdout = "",
        Stderr = "",
        ExitCode = exitCode,
        Truncated = false,
        TimedOut = false,
        DurationMs = 1,
        Cwd = cwd
    };

    public static VirtualFileSystemRegistry Registry(params (FileSystemBackendBase Backend, string MountPoint, ShellReach? Reach)[] mounts)
    {
        var registry = new VirtualFileSystemRegistry();
        foreach (var (backend, mountPoint, reach) in mounts)
        {
            registry.Mount(new FileSystemMount(backend.FilesystemName, mountPoint, backend.DescribeMount) { ShellReach = reach }, backend);
        }

        return registry;
    }

    // Every file tool runs unasked: an agent whose whitelist covers the filesystem feature.
    public static AIFunctionArgumentsWithPermission Whitelisted => new(new ToolPermission(_ => true));

    public static AIFunctionArgumentsWithPermission Allowing(params string[] toolNames) =>
        new(new ToolPermission(name => toolNames.Contains(name)));
}

// The arguments a call arrives with once the approval client has put its view on them.
internal sealed class AIFunctionArgumentsWithPermission : Microsoft.Extensions.AI.AIFunctionArguments
{
    public AIFunctionArgumentsWithPermission(ToolPermission permission)
    {
        Context = new Dictionary<object, object?> { [ToolPermission.ContextKey] = permission };
    }
}