using System.Buffers.Binary;
using System.IO.Compression;
using McpServerSugary.Store;

namespace McpServerSugary.Glucose;

public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Hex(string hex, byte alpha = 255) => new(
        Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16), alpha);
}

// The colours of the app's own tokens (tokens.css), for what is drawn outside it.
public sealed record GlucosePalette(Rgba Good, Rgba Danger, Rgba Caution, Rgba Band, Rgba BandEdge)
{
    public static readonly GlucosePalette Dark = new(
        Rgba.Hex("#8fd3a6"), Rgba.Hex("#ff8a80"), Rgba.Hex("#edc074"), Rgba.Hex("#1f2e24", 200), Rgba.Hex("#3f6b4b"));

    public static readonly GlucosePalette Light = new(
        Rgba.Hex("#2d6a43"), Rgba.Hex("#b3261e"), Rgba.Hex("#7f5200"), Rgba.Hex("#e3efe6", 220), Rgba.Hex("#9cc4a6"));

    public Rgba Of(double mgPerDl, int targetLow, int targetHigh) =>
        mgPerDl < targetLow ? Danger : mgPerDl > targetHigh ? Caution : Good;
}

// A glucose curve as a PNG on a transparent ground, for a home screen widget, which can show an image
// from an address and has no way to draw a series. Her range is the band; the line takes each stretch's
// colour and breaks where readings are further apart than the statistics count as covered, so a gap is
// never drawn as if it had been measured. Loops, not LINQ: this is per pixel.
public static class GlucoseCurvePng
{
    private const double FloorMgPerDl = GlucoseRange.Lowest;
    private const double CeilingAtLeast = GlucoseStatistics.VeryHighAbove;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] _crcTable = [.. Enumerable.Range(0, 256).Select(n =>
        Enumerable.Range(0, 8).Aggregate((uint)n, (c, _) => (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1))];

    public static byte[] Draw(
        IEnumerable<GlucosePoint> readings, DateTimeOffset from, DateTimeOffset to, int targetLow, int targetHigh,
        int width, int height, GlucosePalette palette)
    {
        var points = readings.OrderBy(p => p.At).ToList();
        var pixels = new byte[width * height * 4];
        var ceiling = Math.Max(CeilingAtLeast, points.Count == 0 ? 0 : points.Max(p => p.MgPerDl) + 10);
        var stroke = Math.Max(1.5, height / 70.0);
        var margin = stroke * 2.5;

        double X(DateTimeOffset at) =>
            margin + ((at - from) / (to - from) * (width - 1 - (2 * margin)));

        double Y(double mgPerDl) =>
            height - 1 - margin - ((Math.Clamp(mgPerDl, FloorMgPerDl, ceiling) - FloorMgPerDl) / (ceiling - FloorMgPerDl)
                                   * (height - 1 - (2 * margin)));

        DrawBand(pixels, width, (int)Math.Round(Y(targetHigh)), (int)Math.Round(Y(targetLow)), palette);

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var joinsNext = i + 1 < points.Count && points[i + 1].At - point.At <= GlucoseStatistics.Cap;
            var joinsLast = i > 0 && point.At - points[i - 1].At <= GlucoseStatistics.Cap;
            if (joinsNext)
            {
                var next = points[i + 1];
                var colour = palette.Of((point.MgPerDl + next.MgPerDl) / 2, targetLow, targetHigh);
                DrawSegment(pixels, width, height, X(point.At), Y(point.MgPerDl), X(next.At), Y(next.MgPerDl), stroke / 2, colour);
            }
            else if (!joinsLast)
            {
                var colour = palette.Of(point.MgPerDl, targetLow, targetHigh);
                DrawSegment(pixels, width, height, X(point.At), Y(point.MgPerDl), X(point.At), Y(point.MgPerDl), stroke, colour);
            }
        }

        if (points.Count > 0)
        {
            var newest = points[^1];
            var (x, y) = (X(newest.At), Y(newest.MgPerDl));
            DrawSegment(pixels, width, height, x, y, x, y, stroke * 1.5, palette.Of(newest.MgPerDl, targetLow, targetHigh));
        }

        return Encode(pixels, width, height);
    }

    private static void DrawBand(byte[] pixels, int width, int top, int bottom, GlucosePalette palette)
    {
        var height = pixels.Length / (width * 4);
        for (var y = Math.Max(0, top); y <= Math.Min(height - 1, bottom); y++)
        {
            var colour = y == top || y == bottom ? palette.BandEdge : palette.Band;
            for (var x = 0; x < width; x++)
            {
                Blend(pixels, (y * width * 4) + (x * 4), colour, 1);
            }
        }
    }

    // A round-ended line, its edge softened by how far each pixel's centre is from it.
    private static void DrawSegment(
        byte[] pixels, int width, int height, double x0, double y0, double x1, double y1, double radius, Rgba colour)
    {
        var reach = radius + 1;
        var (left, right) = ((int)Math.Max(0, Math.Floor(Math.Min(x0, x1) - reach)), (int)Math.Min(width - 1, Math.Ceiling(Math.Max(x0, x1) + reach)));
        var (top, bottom) = ((int)Math.Max(0, Math.Floor(Math.Min(y0, y1) - reach)), (int)Math.Min(height - 1, Math.Ceiling(Math.Max(y0, y1) + reach)));
        var (dx, dy) = (x1 - x0, y1 - y0);
        var lengthSquared = (dx * dx) + (dy * dy);

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var along = lengthSquared == 0 ? 0 : Math.Clamp((((x - x0) * dx) + ((y - y0) * dy)) / lengthSquared, 0, 1);
                var (nearX, nearY) = (x0 + (along * dx), y0 + (along * dy));
                var distance = Math.Sqrt(((x - nearX) * (x - nearX)) + ((y - nearY) * (y - nearY)));
                var coverage = Math.Clamp(radius + 0.5 - distance, 0, 1);
                if (coverage > 0)
                {
                    Blend(pixels, (y * width * 4) + (x * 4), colour, coverage);
                }
            }
        }
    }

    private static void Blend(byte[] pixels, int at, Rgba colour, double coverage)
    {
        var over = colour.A / 255.0 * coverage;
        var under = pixels[at + 3] / 255.0;
        var alpha = over + (under * (1 - over));
        if (alpha <= 0)
        {
            return;
        }

        byte Mix(byte top, byte bottom) => (byte)Math.Round(((top * over) + (bottom * under * (1 - over))) / alpha);

        pixels[at] = Mix(colour.R, pixels[at]);
        pixels[at + 1] = Mix(colour.G, pixels[at + 1]);
        pixels[at + 2] = Mix(colour.B, pixels[at + 2]);
        pixels[at + 3] = (byte)Math.Round(alpha * 255);
    }

    private static byte[] Encode(byte[] pixels, int width, int height)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var row = 0; row < height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write(pixels, row * width * 4, width * 4);
            }
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;

        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(type);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~Crc(Crc(uint.MaxValue, type), data));
        png.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc = _crcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}