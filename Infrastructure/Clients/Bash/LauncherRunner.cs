using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;

namespace Infrastructure.Clients.Bash;

// The container's runner. The server runs as a uid of its own so a command cannot read, trace or
// kill it, which means it cannot start a command as PUID either: it asks the root launcher
// (sandbox-runtime/) over a socket only its uid may open. One connection per command, one JSON
// line each way, and hanging up is the cancellation — the launcher kills the tree when its end
// closes. Timeout, output cap and kill-tree are the launcher's to enforce, with the values
// resolved here exactly as the in-process runner resolves them.
public class LauncherRunner(BashRunnerOptions options, string socketPath) : ICommandRunner
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CommandCwd _cwd = new(options.ContainerRoot);

    public async Task<FsResult<FsExecResult>> RunAsync(
        string path, string command, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!_cwd.Resolve(path).TryGetValue(out var cwd, out var unresolved))
        {
            return new FsResult<FsExecResult>.Err(unresolved);
        }

        var request = new LauncherRequest(
            command,
            cwd,
            CommandCwd.EffectiveTimeoutSeconds(options, timeoutSeconds),
            options.OutputCapBytes,
            options.Environment ?? new Dictionary<string, string>());

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var line = JsonSerializer.Serialize(request, _json) + "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? answer;
        try
        {
            answer = await reader.ReadLineAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Closing our end is what tells the launcher to kill the tree.
            socket.Shutdown(SocketShutdown.Both);
            throw;
        }

        return Interpret(answer, cwd);
    }

    private FsResult<FsExecResult> Interpret(string? answer, string cwd)
    {
        if (string.IsNullOrEmpty(answer))
        {
            return FsError.Fail<FsExecResult>(
                ToolError.Codes.InternalError, "The sandbox launcher closed the connection without answering.");
        }

        var node = JsonSerializer.Deserialize<LauncherAnswer>(answer, _json);
        if (node is null || node.Error is not null || node.Stdout is null || node.Stderr is null)
        {
            return FsError.Fail<FsExecResult>(
                ToolError.Codes.InternalError,
                $"The sandbox launcher could not run the command: {node?.Error ?? answer}");
        }

        return new FsResult<FsExecResult>.Ok(new FsExecResult
        {
            Stdout = node.Stdout,
            Stderr = node.Stderr,
            ExitCode = node.TimedOut ? -1 : node.ExitCode,
            TimedOut = node.TimedOut,
            Truncated = node.Truncated,
            DurationMs = node.DurationMs,
            Cwd = _cwd.ToRootRelative(cwd)
        });
    }

    private sealed record LauncherRequest(
        string Command,
        string Cwd,
        int TimeoutSeconds,
        int OutputCapBytes,
        IReadOnlyDictionary<string, string> Env);

    private sealed record LauncherAnswer(
        string? Stdout,
        string? Stderr,
        int ExitCode,
        bool TimedOut,
        bool Truncated,
        long DurationMs,
        string? Error);
}