using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Tests.Integration.Fixtures;

// Short clips made the way a phone's would be, in the containers and codecs phones record, by the
// ffmpeg on this machine — the same tool the Sugary server reads them with, so a test needs nothing
// else. Each is made once per test run and shared. Where ffmpeg is not installed `Missing` says so,
// and a test skips with that reason rather than failing on a machine that cannot make a clip.
public static class TestClips
{
    private static readonly string _directory =
        Path.Combine(Path.GetTempPath(), "sugary-test-clips", Guid.NewGuid().ToString("N"));

    private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _made = new();

    private static readonly Lazy<string?> _missing = new(() =>
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            probe.WaitForExit();
            return probe.ExitCode == 0 ? null : "ffmpeg -version failed on this machine";
        }
        catch (Exception e)
        {
            return $"ffmpeg is not installed on this machine ({e.Message}); the Sugary server reads clips with it";
        }
    });

    public static string? Missing => _missing.Value;

    private const string Picture = "testsrc2=size=1280x720:rate=30";

    // A phone's ordinary clip: H.264 in MP4, with the sound of the table.
    public static Task<byte[]> WithSound(double seconds = 4) => Make(
        $"h264-sound-{seconds}.mp4",
        "-f", "lavfi", "-i", $"{Picture}:duration={Seconds(seconds)}",
        "-f", "lavfi", "-i", $"sine=frequency=440:duration={Seconds(seconds)}",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");

    public static Task<byte[]> Silent() => Make(
        "h264-silent.mp4",
        "-f", "lavfi", "-i", $"{Picture}:duration=3",
        "-c:v", "libx264", "-pix_fmt", "yuv420p");

    // What an iPhone records: HEVC in a QuickTime file, portrait, with sound.
    public static Task<byte[]> HevcPortrait() => Make(
        "hevc-portrait.mov",
        "-f", "lavfi", "-i", "testsrc2=size=720x1280:rate=30:duration=3",
        "-f", "lavfi", "-i", "sine=duration=3",
        "-c:v", "libx265", "-x265-params", "log-level=error", "-tag:v", "hvc1", "-c:a", "aac", "-shortest");

    // What a browser's recorder writes: VP9 and Opus in WebM.
    public static Task<byte[]> WebM() => Make(
        "vp9.webm",
        "-f", "lavfi", "-i", $"{Picture}:duration=3",
        "-f", "lavfi", "-i", "sine=duration=3",
        "-c:v", "libvpx-vp9", "-b:v", "1M", "-deadline", "realtime", "-c:a", "libopus", "-shortest");

    // A phone held upright stores its picture sideways and a flag saying how to turn it.
    public static async Task<byte[]> Rotated()
    {
        var landscape = await File(await Silent(), "rotated-source.mp4");
        return await Make("rotated.mp4", "-display_rotation", "90", "-i", landscape, "-c", "copy");
    }

    // Small and quick to make, since only its length is under test.
    public static Task<byte[]> Long(double seconds) => Make(
        $"long-{seconds}.mp4",
        "-f", "lavfi", "-i", $"testsrc2=size=320x240:rate=10:duration={Seconds(seconds)}",
        "-c:v", "libx264", "-pix_fmt", "yuv420p");

    // Eight seconds whose middle is out of focus: sharp, then blurred over `blurred`, then sharp.
    public static (double From, double To) Blurred => (2.5, 5.5);

    public static Task<byte[]> PartlyBlurred() => Make(
        "partly-blurred.mp4",
        "-f", "lavfi", "-i", $"{Picture}:duration=8",
        "-vf", $"boxblur=12:enable='between(t,{Seconds(Blurred.From)},{Seconds(Blurred.To)})'",
        "-c:v", "libx264", "-pix_fmt", "yuv420p");

    // What ffprobe says of a file's streams and length, for asserting on what the server made.
    public static async Task<JsonNode> ProbeAsync(byte[] bytes)
    {
        var path = await File(bytes, $"probe-{Guid.NewGuid():N}");
        var (output, error, exit) = await RunAsync("ffprobe",
            "-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height:format=duration,format_name",
            "-of", "json", path);
        System.IO.File.Delete(path);
        return exit == 0
            ? JsonNode.Parse(output)!
            : throw new InvalidOperationException($"ffprobe could not read the file: {error}");
    }

    private static Task<byte[]> Make(string name, params string[] arguments) =>
        _made.GetOrAdd(name, _ => new Lazy<Task<byte[]>>(async () =>
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, name);
            var (_, error, exit) = await RunAsync("ffmpeg", ["-v", "error", "-y", .. arguments, path]);
            return exit == 0
                ? await System.IO.File.ReadAllBytesAsync(path)
                : throw new InvalidOperationException($"making the test clip {name} failed: {error}");
        })).Value;

    private static async Task<string> File(byte[] bytes, string name)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        await System.IO.File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static async Task<(string Output, string Error, int Exit)> RunAsync(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        arguments.ToList().ForEach(start.ArgumentList.Add);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (await output, await error, process.ExitCode);
    }

    private static string Seconds(double seconds) => seconds.ToString(CultureInfo.InvariantCulture);
}