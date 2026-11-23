using McpServerSugary.Store;

namespace McpServerSugary.Glucose;

// The colours of the app's own tokens (tokens.css), for what is drawn outside it.
public sealed record GlucosePalette(
    Rgba Good, Rgba Danger, Rgba Caution, Rgba Band, Rgba BandEdge, Rgba Ink, Rgba Muted, Rgba Ground)
{
    public static readonly GlucosePalette Dark = new(
        Rgba.Hex("#8fd3a6"), Rgba.Hex("#ff8a80"), Rgba.Hex("#edc074"), Rgba.Hex("#1f2e24", 200), Rgba.Hex("#3f6b4b"),
        Rgba.Hex("#f7ebdd"), Rgba.Hex("#cdb3a0"), Rgba.Hex("#1c1310", 235));

    public static readonly GlucosePalette Light = new(
        Rgba.Hex("#2d6a43"), Rgba.Hex("#b3261e"), Rgba.Hex("#7f5200"), Rgba.Hex("#e3efe6", 220), Rgba.Hex("#9cc4a6"),
        Rgba.Hex("#3b2219"), Rgba.Hex("#6e4f40"), Rgba.Hex("#fffdf9", 235));

    public Rgba Of(double mgPerDl, int targetLow, int targetHigh) =>
        mgPerDl < targetLow ? Danger : mgPerDl > targetHigh ? Caution : Good;
}

public readonly record struct PlotArea(double Left, double Top, double Width, double Height);

// A glucose curve as a PNG on a transparent ground, for a home screen widget, which can show an image
// from an address and has no way to draw a series. Her range is the band; the line takes each stretch's
// colour and breaks where readings are further apart than the statistics count as covered, so a gap is
// never drawn as if it had been measured. Loops, not LINQ: this is per pixel.
public static class GlucoseCurvePng
{
    private const double FloorMgPerDl = GlucoseRange.Lowest;
    private const double CeilingAtLeast = GlucoseStatistics.VeryHighAbove;

    public static byte[] Draw(
        IEnumerable<GlucosePoint> readings, DateTimeOffset from, DateTimeOffset to, int targetLow, int targetHigh,
        int width, int height, GlucosePalette palette)
    {
        var canvas = new PngCanvas(width, height);
        Plot(canvas, new PlotArea(0, 0, width, height), readings, from, to, targetLow, targetHigh, palette);
        return canvas.ToPng();
    }

    public static void Plot(
        PngCanvas canvas, PlotArea area, IEnumerable<GlucosePoint> readings, DateTimeOffset from, DateTimeOffset to,
        int targetLow, int targetHigh, GlucosePalette palette)
    {
        var points = readings.OrderBy(p => p.At).ToList();
        var ceiling = Math.Max(CeilingAtLeast, points.Count == 0 ? 0 : points.Max(p => p.MgPerDl) + 10);
        var stroke = Math.Max(1.5, area.Height / 70.0);
        var margin = stroke * 2.5;

        double X(DateTimeOffset at) =>
            area.Left + margin + ((at - from) / (to - from) * (area.Width - 1 - (2 * margin)));

        double Y(double mgPerDl) =>
            area.Top + area.Height - 1 - margin
            - ((Math.Clamp(mgPerDl, FloorMgPerDl, ceiling) - FloorMgPerDl) / (ceiling - FloorMgPerDl)
               * (area.Height - 1 - (2 * margin)));

        DrawBand(canvas, area, (int)Math.Round(Y(targetHigh)), (int)Math.Round(Y(targetLow)), palette);

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var joinsNext = i + 1 < points.Count && points[i + 1].At - point.At <= GlucoseStatistics.Cap;
            var joinsLast = i > 0 && point.At - points[i - 1].At <= GlucoseStatistics.Cap;
            if (joinsNext)
            {
                var next = points[i + 1];
                var colour = palette.Of((point.MgPerDl + next.MgPerDl) / 2, targetLow, targetHigh);
                canvas.Line(X(point.At), Y(point.MgPerDl), X(next.At), Y(next.MgPerDl), stroke / 2, colour);
            }
            else if (!joinsLast)
            {
                var colour = palette.Of(point.MgPerDl, targetLow, targetHigh);
                canvas.Line(X(point.At), Y(point.MgPerDl), X(point.At), Y(point.MgPerDl), stroke, colour);
            }
        }

        if (points.Count > 0)
        {
            var newest = points[^1];
            var (x, y) = (X(newest.At), Y(newest.MgPerDl));
            canvas.Line(x, y, x, y, stroke * 1.5, palette.Of(newest.MgPerDl, targetLow, targetHigh));
        }
    }

    private static void DrawBand(PngCanvas canvas, PlotArea area, int top, int bottom, GlucosePalette palette)
    {
        var (left, right) = ((int)Math.Round(area.Left), (int)Math.Round(area.Left + area.Width) - 1);
        for (var y = top; y <= bottom; y++)
        {
            canvas.Row(left, right, y, y == top || y == bottom ? palette.BandEdge : palette.Band);
        }
    }
}