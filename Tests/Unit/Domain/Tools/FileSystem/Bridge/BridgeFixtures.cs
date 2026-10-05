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
internal sealed class ScriptedSandbox(IVfsBridge bridge, Func<VfsCall, CancellationToken, Task<FsExecResult>> script)
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

// A disk root in memory: bytes behind every file, info with a size, a glob over one level, text
// create for the extensions it reads as text and blob writes for anything.
internal class MemoryDisk(string name, IDictionary<string, string> files, string[]? textExtensions = null)
    : FileSystemBackendBase
{
    public IDictionary<string, string> Files { get; } = files;

    public List<string> Writes { get; } = [];

    private readonly string[] _text = textExtensions ?? [".md", ".txt", ".json"];

    // A mount with a rule about what may leave it, as the media library has for a live download.
    public string? RefusesMoveOutOf { get; init; }

    public override Task<FsResult<FsMoveOutCheckResult>> MoveOutCheckAsync(string path, CancellationToken ct) =>
        Task.FromResult(RefusesMoveOutOf is { } refused && path.Trim('/').StartsWith(refused, StringComparison.Ordinal)
            ? FsError.Fail<FsMoveOutCheckResult>(global::Domain.Tools.ToolError.Codes.PermissionDenied, $"{path} cannot leave {name}")
            : FsMoveOutCheckResult.Allow(path));

    public override Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        var (from, to) = (sourcePath.Trim('/'), destinationPath.Trim('/'));
        if (!Files.ContainsKey(from))
        {
            return Task.FromResult(FsError.NotFound<FsMoveResult>(sourcePath));
        }

        if (Files.ContainsKey(to))
        {
            return Task.FromResult(FsError.AlreadyExists<FsMoveResult>(destinationPath));
        }

        Files[to] = Files[from];
        Files.Remove(from);
        Writes.Add($"move {from} {to}");
        return Task.FromResult<FsResult<FsMoveResult>>(new FsResult<FsMoveResult>.Ok(new FsMoveResult
        {
            Status = "moved", Source = sourcePath, Destination = destinationPath, Message = "moved"
        }));
    }

    public override Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var key = path.Trim('/');
        var removed = Files.Keys.Where(k => k == key || k.StartsWith(key + "/", StringComparison.Ordinal)).ToList();
        if (removed.Count == 0)
        {
            return Task.FromResult(FsError.NotFound<FsRemoveResult>(path));
        }

        removed.ForEach(k => Files.Remove(k));
        Writes.Add($"delete {key}");
        return Task.FromResult<FsResult<FsRemoveResult>>(new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "deleted", Message = "deleted", OriginalPath = path, TrashPath = ""
        }));
    }

    public override Task<FsResult<FsCreateResult>> CreateAsync(
        string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var key = path.Trim('/');
        if (!_text.Contains(Path.GetExtension(key)))
        {
            return Task.FromResult(FsError.Invalid<FsCreateResult>($"Extension not allowed: {Path.GetExtension(key)}"));
        }

        if (!overwrite && Files.ContainsKey(key))
        {
            return Task.FromResult(FsError.AlreadyExists<FsCreateResult>(path));
        }

        Files[key] = content;
        Writes.Add($"create {key}");
        return Task.FromResult<FsResult<FsCreateResult>>(new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "created", FilePath = path, Size = $"{content.Length}B", Lines = content.Split('\n').Length
        }));
    }

    public override async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            bytes.Write(chunk.Span);
        }

        Files[path.Trim('/')] = Encoding.Latin1.GetString(bytes.ToArray());
        Writes.Add($"blob {path.Trim('/')}");
        return bytes.Length;
    }

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
    public List<string> Deleted { get; } = [];

    // A timer's shape: its directory is the thing to remove, its rendered files refuse alone.
    public override Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var key = path.Trim('/');
        if (files.ContainsKey(key))
        {
            return Task.FromResult(FsError.Invalid<FsRemoveResult>($"Cancel it by deleting its directory: /{key[..key.IndexOf('/')]}"));
        }

        var removed = files.Keys.Where(k => k.StartsWith(key + "/", StringComparison.Ordinal)).ToList();
        if (removed.Count == 0)
        {
            return Task.FromResult(FsError.NotFound<FsRemoveResult>(path));
        }

        removed.ForEach(k => files.Remove(k));
        Deleted.Add(key);
        return Task.FromResult<FsResult<FsRemoveResult>>(new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "deleted", Message = "cancelled", OriginalPath = path, TrashPath = ""
        }));
    }

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
    public static AIFunctionArgumentsWithPermission Whitelisted => new(Everything);

    public static ToolPermission Everything => new(_ => true);

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