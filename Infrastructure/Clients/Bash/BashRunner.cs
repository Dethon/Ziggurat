using System.Diagnostics;
using System.Text;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Files;

namespace Infrastructure.Clients.Bash;

// The in-process runner: a child of the server, as the server's own user. The container runs the
// launcher-backed one instead (LauncherRunner); this stays for in-process hosts — the integration
// fixtures and the outpost — and both resolve and report the working directory the same way.
public class BashRunner(BashRunnerOptions options) : ICommandRunner
{
    private readonly CommandCwd _cwd = new(options.ContainerRoot);

    public async Task<FsResult<FsExecResult>> RunAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        if (!_cwd.Resolve(path).TryGetValue(out var cwd, out var unresolved))
        {
            return new FsResult<FsExecResult>.Err(unresolved);
        }

        var effectiveTimeout = TimeSpan.FromSeconds(CommandCwd.EffectiveTimeoutSeconds(options, timeoutSeconds));

        var psi = new ProcessStartInfo("bash")
        {
            ArgumentList = { "-lc", command },
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (options.Environment is { } environment)
        {
            psi.Environment.Clear();
            foreach (var (name, value) in environment)
            {
                psi.Environment[name] = value;
            }
        }

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
            Cwd = _cwd.ToRootRelative(cwd)
        });
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