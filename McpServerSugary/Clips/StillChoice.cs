namespace McpServerSugary.Clips;

// A moment of the clip and how blurred it is there: ffmpeg's blurdetect, the width of its edges, so
// larger is blurrier. NaN where the frame has no edges at all (a lens cap, a blank wall), which is
// worth no more as a still than a blurred one.
public sealed record FrameSample(double At, double Blur);

// Which moments of a clip become its stills. The clip is cut into as many stretches as stills are
// wanted, so they are spread evenly over it, and each stretch gives its sharpest moment. A stretch
// whose sharpest moment is still blurred against the clip's sharpest (more than `Tolerance` times as
// blurred) gives none: a model shown a smear learns nothing from it and may guess from it anyway.
// The sharpest stretch always gives one, so a clip that is blurred throughout still has a still.
public static class StillChoice
{
    public const double Tolerance = 1.6;

    public static IReadOnlyList<double> Pick(IReadOnlyList<FrameSample> samples, int stills)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0 || stills <= 0)
        {
            return [];
        }

        var stretches = Math.Min(stills, samples.Count);
        var sharpest = Enumerable.Range(0, stretches)
            .Select(k => samples
                .Skip(k * samples.Count / stretches)
                .Take((k + 1) * samples.Count / stretches - k * samples.Count / stretches)
                .MinBy(Blur)!)
            .ToList();

        var reference = sharpest.Min(Blur);
        return double.IsPositiveInfinity(reference)
            ? [.. sharpest.Select(s => s.At)]
            : [.. sharpest.Where(s => Blur(s) <= reference * Tolerance).Select(s => s.At)];
    }

    private static double Blur(FrameSample sample) =>
        double.IsNaN(sample.Blur) ? double.PositiveInfinity : sample.Blur;
}