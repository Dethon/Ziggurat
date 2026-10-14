using System.Globalization;
using System.Text.Json;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Clips;

public abstract record PreparedClip
{
    // Said to her as written: the reason the meal could not be estimated from this clip.
    public sealed record Refused(string Reason) : PreparedClip;

    public sealed record Ready(byte[] Clip, IReadOnlyList<Still> Stills) : PreparedClip;
}

// A picture of the plate taken from the clip, and the moment of the clip it was taken at.
public sealed record Still(double At, byte[] Jpeg);

// What a clip from her phone becomes before anything is kept or sent: the one shape every model that
// watches video takes — H.264 in MP4, its short side at most 540 lines, no sound, at most twenty
// seconds — and about eight sharp stills spread over it, at the size the app shrinks a photo to, for
// a model that only sees pictures. The stills are taken from what the phone recorded, not from the
// shrunk clip, so they are as sharp as a photo would be.
//
// The sound is dropped here and never stored: what is said at the table is nobody's business. So is
// everything the phone wrote about the clip besides the picture (where it was filmed, on what).
public sealed class ClipProcessor(ClipSettings settings, ClipTools tools, ILogger<ClipProcessor> logger)
{
    // A phone asked for twenty seconds stops a few frames later.
    private const double Grace = 0.5;

    // Moments looked at per still wanted, so each stretch of the clip has a few to choose from.
    private const int SamplesPerStill = 4;

    private const string Unreadable = "That clip could not be read. Film it again, or take a photo instead.";

    public async Task<PreparedClip> PrepareAsync(IFormFile clip, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "sugary-clips", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var original = Path.Combine(work, "original");
            await using (var file = File.Create(original))
            {
                await clip.CopyToAsync(file, ct);
            }

            return await PrepareAsync(original, work, ct);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Removing the clip work directory {Directory} failed", work);
            }
        }
    }

    private async Task<PreparedClip> PrepareAsync(string original, string work, CancellationToken ct)
    {
        if (await DurationAsync(original, ct) is not { } seconds)
        {
            return new PreparedClip.Refused(Unreadable);
        }

        if (seconds > settings.MaxSeconds + Grace)
        {
            return new PreparedClip.Refused(
                $"The clip is {Number(Math.Round(seconds))} seconds long. Film at most {Number(settings.MaxSeconds)} " +
                "seconds around the plate, or take a photo instead.");
        }

        var normalised = Path.Combine(work, "clip.mp4");
        var made = await tools.FfmpegAsync(
        [
            .. ClipTools.InputLimits, "-i", original,
            "-t", Number(settings.MaxSeconds),
            "-map", "0:v:0", "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            "-vf", ShortSide(settings.MaxLines), "-fpsmax", "30",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "28", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", "-f", "mp4", normalised
        ], ct);
        if (made.ExitCode != 0 || !File.Exists(normalised))
        {
            logger.LogInformation("A clip could not be made small: {Error}", made.Error);
            return new PreparedClip.Refused(Unreadable);
        }

        var length = Math.Min(seconds, settings.MaxSeconds);
        var moments = StillChoice.Pick(await SampleAsync(original, length, ct), settings.Stills);
        var stills = new List<Still>();
        foreach (var (at, index) in moments.Select((at, i) => (at, i)))
        {
            if (await StillAsync(original, at, Path.Combine(work, $"still-{index}.jpg"), ct) is { } jpeg)
            {
                stills.Add(new Still(at, jpeg));
            }
        }

        return stills.Count == 0
            ? new PreparedClip.Refused(Unreadable)
            : new PreparedClip.Ready(await File.ReadAllBytesAsync(normalised, ct), stills);
    }

    // Null when the file is not a clip at all: not a container phones record in, or nothing in it
    // to watch. The container's own length where it states one, and otherwise the last frame's time,
    // since a browser's recorder writes WebM without it.
    private async Task<double?> DurationAsync(string original, CancellationToken ct)
    {
        var probe = await tools.ProbeAsync(
            ["-show_entries", "format=duration:stream=codec_type", "-of", "json", original], ct);
        if (probe.ExitCode != 0)
        {
            return null;
        }

        using var read = JsonDocument.Parse(probe.Output);
        var hasPicture = read.RootElement.TryGetProperty("streams", out var streams)
                         && streams.EnumerateArray().Any(s =>
                             s.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
        if (!hasPicture)
        {
            return null;
        }

        if (read.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var stated)
            && Parse(stated.GetString()) is { } duration)
        {
            return duration;
        }

        var packets = await tools.ProbeAsync(
            ["-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", original], ct);
        var times = packets.Output.Split('\n').Select(line => Parse(line.Trim().TrimEnd(','))).OfType<double>().ToList();
        return times.Count > 0 ? times.Max() : null;
    }

    // How blurred the clip is at a few moments per still wanted, measured on a small copy of each
    // frame since only the comparison between them matters.
    private async Task<IReadOnlyList<FrameSample>> SampleAsync(string original, double seconds, CancellationToken ct)
    {
        var rate = Math.Clamp(settings.Stills * SamplesPerStill / Math.Max(seconds, 0.1), 0.5, 10);
        var run = await tools.FfmpegAsync(
        [
            .. ClipTools.InputLimits, "-t", Number(settings.MaxSeconds), "-i", original,
            "-map", "0:v:0", "-an",
            "-vf", $"fps={Number(rate)},{LongSide(640)},blurdetect,metadata=print:file=-",
            "-f", "null", "-"
        ], ct);
        if (run.ExitCode != 0)
        {
            logger.LogInformation("A clip's frames could not be measured: {Error}", run.Error);
        }

        return Samples(run.Output);
    }

    // metadata=print writes each frame's time on one line and what blurdetect measured on the next.
    private static List<FrameSample> Samples(string printed)
    {
        var samples = new List<FrameSample>();
        double? at = null;
        foreach (var line in printed.Split('\n').Select(l => l.Trim()))
        {
            var time = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (time >= 0)
            {
                at = Parse(line[(time + "pts_time:".Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
            }
            else if (line.StartsWith("lavfi.blur=", StringComparison.Ordinal) && at is { } moment)
            {
                samples.Add(new FrameSample(moment, Parse(line["lavfi.blur=".Length..]) ?? double.NaN));
                at = null;
            }
        }

        return samples;
    }

    private async Task<byte[]?> StillAsync(string original, double at, string path, CancellationToken ct)
    {
        var run = await tools.FfmpegAsync(
        [
            .. ClipTools.InputLimits, "-ss", Number(at), "-i", original,
            "-map", "0:v:0", "-frames:v", "1", "-map_metadata", "-1",
            "-vf", $"scale=w='min({settings.StillEdge},iw)':h='min({settings.StillEdge},ih)':force_original_aspect_ratio=decrease",
            "-q:v", "3", "-f", "mjpeg", path
        ], ct);
        if (run.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length == 0)
        {
            logger.LogInformation("No still could be taken at {At} s: {Error}", at, run.Error);
            return null;
        }

        return await File.ReadAllBytesAsync(path, ct);
    }

    // The short side held to `lines` and never enlarged, the other following it, both even as H.264
    // wants. A phone held upright is turned upright before this (ffmpeg applies the rotation flag),
    // so a portrait clip stays portrait.
    private static string ShortSide(int lines) =>
        $"scale=w='if(gte(iw,ih),-2,min({lines},trunc(iw/2)*2))':h='if(gte(iw,ih),min({lines},trunc(ih/2)*2),-2)'";

    private static string LongSide(int edge) =>
        $"scale=w='if(gte(iw,ih),{edge},-2)':h='if(gte(iw,ih),-2,{edge})'";

    private static double? Parse(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}