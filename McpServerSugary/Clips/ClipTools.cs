using System.ComponentModel;
using System.Diagnostics;
using McpServerSugary.Settings;

namespace McpServerSugary.Clips;

// The two tools a clip is read and made with, run as processes of the server's image. Every input
// is read through the demuxers phones record in alone (QuickTime/MP4 and Matroska/WebM) and from a
// local file alone, so a file shaped as a playlist or a concat list cannot make ffmpeg read anything
// but the clip itself.
public sealed class ClipTools(ClipSettings settings)
{
    public static readonly string[] InputLimits =
        ["-format_whitelist", "mov,matroska", "-protocol_whitelist", "file"];

    public sealed record Run(int ExitCode, string Output, string Error);

    public Task<Run> ProbeAsync(IEnumerable<string> arguments, CancellationToken ct) =>
        RunAsync(settings.Ffprobe, ["-v", "error", .. InputLimits, .. arguments], ct);

    public Task<Run> FfmpegAsync(IEnumerable<string> arguments, CancellationToken ct) =>
        RunAsync(settings.Ffmpeg, ["-v", "error", "-nostdin", "-y", .. arguments], ct);

    private async Task<Run> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        arguments.ToList().ForEach(start.ArgumentList.Add);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new ClipToolsUnavailableException($"{tool} did not start");
        }
        catch (Win32Exception e)
        {
            throw new ClipToolsUnavailableException($"{tool} could not be started: {e.Message}", e);
        }

        // The time a run may take is the clip's, which the caller's token carries (ClipProcessor).
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
                return new Run(process.ExitCode, await output, await error);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
        }
    }
}

// The server cannot read clips at all just now: the tools are missing from its image, or reading
// one ran past its time. Not the clip's fault, so not said as a refusal of it.
public sealed class ClipToolsUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);