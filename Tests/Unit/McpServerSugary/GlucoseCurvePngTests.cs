using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using McpServerSugary.Glucose;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// What the widget's curve must not get wrong: a stretch with no readings never looks measured, and a low
// is drawn as one. Read back from the PNG itself, so the encoding is proved by a real inflate too.
public class GlucoseCurvePngTests
{
    private const int Width = 200;
    private const int Height = 100;

    private static readonly DateTimeOffset _to = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _from = _to.AddHours(-1);

    private static GlucosePoint At(int minutesAfterStart, double mgPerDl) => new(_from.AddMinutes(minutesAfterStart), mgPerDl);

    private static byte[] Pixels(byte[] png)
    {
        using var compressed = new MemoryStream();
        for (var at = 8; at < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at, 4));
            if (Encoding.ASCII.GetString(png, at + 4, 4) == "IDAT")
            {
                compressed.Write(png, at + 8, length);
            }

            at += 12 + length;
        }

        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var rows = raw.ToArray();
        rows.Length.ShouldBe(Height * ((Width * 4) + 1));
        return [.. Enumerable.Range(0, Height).SelectMany(row => rows.Skip((row * ((Width * 4) + 1)) + 1).Take(Width * 4))];
    }

    // Near the colour rather than equal to it: a thin line's edge is softened into what lies under it.
    private static bool Is(byte[] pixels, int x, int y, Rgba colour)
    {
        const int near = 60;
        var at = ((y * Width) + x) * 4;
        return Math.Abs(pixels[at] - colour.R) <= near && Math.Abs(pixels[at + 1] - colour.G) <= near
               && Math.Abs(pixels[at + 2] - colour.B) <= near && pixels[at + 3] >= 128;
    }

    private static byte[] Draw(params GlucosePoint[] points) =>
        Pixels(GlucoseCurvePng.Draw(points, _from, _to, 70, 180, Width, Height, GlucosePalette.Dark));

    [Fact]
    public void AStretchWithNoReadings_IsNotDrawnAcross()
    {
        var pixels = Draw(At(0, 120), At(5, 120), At(10, 120), At(50, 120), At(55, 120), At(60, 120));

        Enumerable.Range(0, Height).ShouldNotContain(y => Is(pixels, Width / 2, y, GlucosePalette.Dark.Good));
        Enumerable.Range(0, Height).ShouldContain(y => Is(pixels, Width / 10, y, GlucosePalette.Dark.Good));
    }

    [Fact]
    public void ALow_IsDrawnInTheDangerColour()
    {
        var pixels = Draw(At(10, 60), At(15, 60), At(20, 60));

        Enumerable.Range(0, Height).ShouldContain(y => Is(pixels, Width / 4, y, GlucosePalette.Dark.Danger));
    }

    [Fact]
    public void ALoneReading_IsStillShown()
    {
        var pixels = Draw(At(30, 120));

        Enumerable.Range(0, Height).ShouldContain(y => Is(pixels, Width / 2, y, GlucosePalette.Dark.Good));
    }

    [Fact]
    public void OutsideHerRange_TheGroundIsTransparent()
    {
        var pixels = Draw(At(30, 120));

        pixels[3].ShouldBe((byte)0);
    }
}