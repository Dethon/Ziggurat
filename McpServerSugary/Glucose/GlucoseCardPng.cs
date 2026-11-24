using System.Globalization;
using Domain.DTOs.Sugary;

namespace McpServerSugary.Glucose;

// `Time` is the reading's clock time rather than its age: a picture the widget stopped refreshing keeps
// saying when it was true, where an age drawn into it would stay "1 min" forever.
public sealed record GlucoseCard(string Value, string Unit, GlucoseTrend Trend, string Time, double? MgPerDl);

// The widget's one picture: the reading, its trend and time above the curve. KWGT re-fetches an image
// whose address changes but leaves a text's web formula on its cache, so a number only stays current
// by being drawn here. It has its own ground because a number has to be read over any wallpaper.
public static class GlucoseCardPng
{
    private const double HeaderShare = 0.40;
    private const double StrokeShare = 0.36;
    private const double UnitShare = 0.45;
    private const double TimeShare = 0.62;
    private const double Gap = 1.6;
    private const double ArrowWidth = 5;

    public static byte[] Draw(
        GlucoseCard card, IEnumerable<GlucosePoint> readings, DateTimeOffset from, DateTimeOffset to,
        int targetLow, int targetHigh, int width, int height, GlucosePalette palette)
    {
        var canvas = new PngCanvas(width, height);
        canvas.FillRounded(Math.Min(width, height) * 0.08, palette.Ground);
        var pad = Math.Max(4, height * 0.06);
        var headerBottom = height * HeaderShare;

        DrawHeader(canvas, card, pad, headerBottom, targetLow, targetHigh, palette);
        GlucoseCurvePng.Plot(
            canvas, new PlotArea(pad, headerBottom, width - (2 * pad), height - headerBottom - pad),
            readings, from, to, targetLow, targetHigh, palette);
        return canvas.ToPng();
    }

    private static void DrawHeader(
        PngCanvas canvas, GlucoseCard card, double pad, double headerBottom, int targetLow, int targetHigh,
        GlucosePalette palette)
    {
        var inset = pad * 1.4;
        var baseline = headerBottom - (pad * 0.4);
        var tall = (baseline - pad) / StrokeFont.CapHeight;
        var hasArrow = ArrowAngle(card.Trend) is not null;
        var unitsWide = StrokeFont.Width(card.Value) + (hasArrow ? ArrowWidth + Gap : 0) + Gap
                        + (StrokeFont.Width(card.Unit) * UnitShare) + (Gap * 2) + (StrokeFont.Width(card.Time) * TimeShare);
        var size = Math.Min(tall, (canvas.Width - (2 * inset)) / unitsWide);

        var colour = card.MgPerDl is { } mgPerDl ? palette.Of(mgPerDl, targetLow, targetHigh) : palette.Muted;
        var x = inset;
        StrokeFont.Draw(canvas, card.Value, x, baseline, size, size * StrokeShare, colour);
        x += (StrokeFont.Width(card.Value) + Gap) * size;

        if (ArrowAngle(card.Trend) is { } angle)
        {
            DrawArrow(canvas, x + (ArrowWidth * size / 2), baseline - (StrokeFont.CapHeight * size / 2), size, angle, colour);
            x += (ArrowWidth + Gap) * size;
        }

        var small = size * UnitShare;
        StrokeFont.Draw(canvas, card.Unit, x, baseline, small, Math.Max(0.7, small * StrokeShare), palette.Muted);

        var time = size * TimeShare;
        StrokeFont.Draw(
            canvas, card.Time, canvas.Width - inset - (StrokeFont.Width(card.Time) * time), baseline, time,
            Math.Max(0.7, time * StrokeShare), palette.Ink);
    }

    private static double? ArrowAngle(GlucoseTrend trend) => trend switch
    {
        GlucoseTrend.RisingFast => -90,
        GlucoseTrend.Rising => -45,
        GlucoseTrend.Steady => 0,
        GlucoseTrend.Falling => 45,
        GlucoseTrend.FallingFast => 90,
        _ => null
    };

    private static void DrawArrow(PngCanvas canvas, double x, double y, double size, double degrees, Rgba colour)
    {
        const double reach = 2.3;
        const double barb = 1.7;
        const double barbTurn = 140;
        var radius = size * StrokeShare;

        (double X, double Y) Along(double fromX, double fromY, double angle, double length) =>
            (fromX + (Math.Cos(angle * Math.PI / 180) * length * size), fromY + (Math.Sin(angle * Math.PI / 180) * length * size));

        var tip = Along(x, y, degrees, reach);
        var tail = Along(x, y, degrees, -reach);
        var left = Along(tip.X, tip.Y, degrees + barbTurn, barb);
        var right = Along(tip.X, tip.Y, degrees - barbTurn, barb);
        canvas.Line(tail.X, tail.Y, tip.X, tip.Y, radius, colour);
        canvas.Line(tip.X, tip.Y, left.X, left.Y, radius, colour);
        canvas.Line(tip.X, tip.Y, right.X, right.Y, radius, colour);
    }
}

// Only what a reading, a unit and a time need, as strokes, so the picture depends on no font being in the
// image. Glyphs are polylines on a grid six high, baseline at 6, with `|` between separate strokes.
internal static class StrokeFont
{
    public const double CapHeight = 6;

    private const double Spacing = 1.25;

    private static readonly Dictionary<char, Glyph> _glyphs = new Dictionary<char, string>
    {
        ['0'] = "1,0 3,0 4,1 4,5 3,6 1,6 0,5 0,1 1,0",
        ['1'] = "0.4,1.3 2,0 2,6",
        ['2'] = "0,1 1,0 3,0 4,1 4,2 0,6 4,6",
        ['3'] = "0,1 1,0 3,0 4,1 4,2 3,3 1.5,3|3,3 4,4 4,5 3,6 1,6 0,5",
        ['4'] = "3.2,6 3.2,0 0,4.2 4,4.2",
        ['5'] = "3.8,0 0.5,0 0.2,2.7 1,2.2 3,2.2 4,3.2 4,5 3,6 1,6 0,5",
        ['6'] = "3.5,0 1.6,0 0,2 0,5 1,6 3,6 4,5 4,3.8 3,2.8 1,2.8 0,3.8",
        ['7'] = "0,0 4,0 1.4,6",
        ['8'] = "1,3 0,2 0,1 1,0 3,0 4,1 4,2 3,3 1,3 0,4 0,5 1,6 3,6 4,5 4,4 3,3",
        ['9'] = "0.5,6 2.4,6 4,4 4,1 3,0 1,0 0,1 0,2.2 1,3.2 3,3.2 4,2.2",
        ['.'] = "0.4,6 0.4,6",
        [':'] = "0.4,1.7 0.4,1.7|0.4,4.6 0.4,4.6",
        ['-'] = "0,3.2 2.8,3.2",
        ['/'] = "0,6.3 2.6,-0.3",
        ['m'] = "0,2.5 0,6|0,3.2 0.8,2.5 1.7,2.5 2.5,3.2 2.5,6|2.5,3.2 3.3,2.5 4.2,2.5 5,3.2 5,6",
        ['g'] = "3.4,3.2 2.7,2.5 0.7,2.5 0,3.2 0,4.8 0.7,5.5 2.7,5.5 3.4,4.8|3.4,2.5 3.4,6.9 2.7,7.6 0.7,7.6 0,7.1",
        ['d'] = "3.4,3.2 2.7,2.5 0.7,2.5 0,3.2 0,5.3 0.7,6 2.7,6 3.4,5.3|3.4,0 3.4,6",
        ['o'] = "0.7,2.5 2.7,2.5 3.4,3.2 3.4,5.3 2.7,6 0.7,6 0,5.3 0,3.2 0.7,2.5",
        ['l'] = "0.2,0 0.2,6",
        ['L'] = "0,0 0,6 3,6"
    }.ToDictionary(g => g.Key, g => Glyph.Parse(g.Value));

    public static double Width(string text) =>
        text.Sum(c => _glyphs[c].Width + Spacing) - (text.Length > 0 ? Spacing : 0);

    public static void Draw(PngCanvas canvas, string text, double x, double baseline, double size, double radius, Rgba colour)
    {
        var top = baseline - (CapHeight * size);
        foreach (var glyph in text.Select(c => _glyphs[c]))
        {
            foreach (var stroke in glyph.Strokes)
            {
                foreach (var (from, to) in stroke.Zip(stroke.Skip(1)))
                {
                    canvas.Line(x + (from.X * size), top + (from.Y * size), x + (to.X * size), top + (to.Y * size), radius, colour);
                }
            }

            x += (glyph.Width + Spacing) * size;
        }
    }

    private sealed record Glyph(IReadOnlyList<IReadOnlyList<(double X, double Y)>> Strokes, double Width)
    {
        public static Glyph Parse(string source)
        {
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes =
            [
                .. source.Split('|').Select(stroke => (IReadOnlyList<(double X, double Y)>)
                [
                    .. stroke.Split(' ').Select(point => point.Split(',')).Select(xy =>
                        (double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture)))
                ])
            ];
            return new Glyph(strokes, strokes.SelectMany(s => s).Max(p => p.X));
        }
    }
}