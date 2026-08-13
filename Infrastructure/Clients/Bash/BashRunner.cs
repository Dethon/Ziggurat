using System.Diagnostics;
using System.Text;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Files;

namespace Infrastructure.Clients.Bash;

public class BashRunner(BashRunnerOptions options) : ICommandRunner
{
    // The runner's own coordinates. Every working directory is resolved against this root and
    // reported relative to it; which mount point goes in front is the exec tool's business, not
    // this one's. The jail is the same containment decision the file tools make, so a path that
    // climbs out — or reaches out through a symlink — lands back at the root rather than
    // somewhere the response cannot name.
    private readonly PathJail _jail = new(options.ContainerRoot);

    public async Task<FsResult<FsExecResult>> RunAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        if (!TryResolveCwd(path, out var cwd))
        {
            return FsError.Invalid<FsExecResult>(_jail.DeniedMessage);
        }

        if (!Directory.Exists(cwd))
        {
            return FsError.Fail<FsExecResult>(
                ToolError.Codes.NotFound,
                $"Working directory '{cwd}' does not exist or is not a directory.");
        }

        var effectiveTimeout = TimeSpan.FromSeconds(
            Math.Clamp(timeoutSeconds ?? options.DefaultTimeoutSeconds, 1, options.MaxTimeoutSeconds));

        var psi = new ProcessStartInfo("bash")
        {
            ArgumentList = { "-lc", command },
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        var stdoutTask = ReadCappedAsync(process.StandardOutput, options.OutputCapBytes, ct);
        var stderrTask = ReadCappedAsync(process.StandardError, options.OutputCapBytes, ct);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try
            { process.Kill(entireProcessTree: true); }
            catch { /* already exited */ }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            { process.Kill(entireProcessTree: true); }
            catch { /* already exited */ }
            try
            { await process.WaitForExitAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            try
            { await stdoutTask; }
            catch { /* drain reader before process disposal */ }
            try
            { await stderrTask; }
            catch { /* drain reader before process disposal */ }
            throw;
        }

        var stdoutResult = await stdoutTask;
        var stderrResult = await stderrTask;

        return new FsResult<FsExecResult>.Ok(new FsExecResult
        {
            Stdout = stdoutResult.Text,
            Stderr = stderrResult.Text,
            ExitCode = timedOut ? -1 : process.ExitCode,
            TimedOut = timedOut,
            Truncated = stdoutResult.Truncated || stderrResult.Truncated,
            DurationMs = sw.ElapsedMilliseconds,
            Cwd = ToRootRelative(cwd)
        });
    }

    // Total: every path names a place under the root, including the empty one and an absolute one.
    // There is no home-directory case — the mount point means the container root here as it does in
    // glob, read, search and info.
    //
    // A path that still climbs out is refused rather than clamped. Redirecting it would answer for
    // a directory the caller never asked for, which is the shape of mistake this whole change
    // exists to remove; with the root at "/" nothing can climb out anyway.
    private bool TryResolveCwd(string path, out string cwd)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        cwd = Path.GetFullPath(Path.Combine(_jail.Root, normalized));

        return _jail.Contains(cwd);
    }

    // The root reports as the empty path, which is the mount point with a trailing slash once the
    // tool has prefixed it — the spelling glob already uses for a directory.
    private string ToRootRelative(string cwd)
    {
        var relative = Path.GetRelativePath(_jail.Root, cwd);
        return relative == "." ? "" : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(
        StreamReader reader, int capBytes, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var byteCount = 0;
        var truncated = false;
        var buffer = new char[4096];

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (read == 0)
            {
                break;
            }

            if (truncated)
            {
                continue;
            }

            var chunkBytes = Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (byteCount + chunkBytes <= capBytes)
            {
                sb.Append(buffer, 0, read);
                byteCount += chunkBytes;
                continue;
            }

            var remaining = capBytes - byteCount;
            var taken = 0;
            var fitting = buffer.Take(read)
                .Select(c => (Char: c, Bytes: Encoding.UTF8.GetByteCount(new[] { c })))
                .TakeWhile(x =>
                {
                    if (taken + x.Bytes > remaining)
                    {
                        return false;
                    }
                    taken += x.Bytes;
                    return true;
                })
                .Select(x => x.Char)
                .ToArray();
            sb.Append(fitting);
            byteCount += taken;
            truncated = true;
        }

        return (sb.ToString(), truncated);
    }
}