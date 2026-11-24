using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using McpServerSugary.Glucose;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// A drawn PNG read back through a real inflate, so a test of what it shows proves its encoding too.
internal sealed class PngPixels
{
    private readonly byte[] _pixels;

    public int Width { get; }

    public int Height { get; }

    private PngPixels(byte[] pixels, int width, int height) => (_pixels, Width, Height) = (pixels, width, height);

    public static PngPixels Read(byte[] png)
    {
        var (width, height) = (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
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
        var stride = width * 4;
        rows.Length.ShouldBe(height * (stride + 1));
        var pixels = new byte[height * stride];
        for (var row = 0; row < height; row++)
        {
            Array.Copy(rows, (row * (stride + 1)) + 1, pixels, row * stride, stride);
        }

        return new PngPixels(pixels, width, height);
    }

    public byte Alpha(int x, int y) => _pixels[(((y * Width) + x) * 4) + 3];

    // Near the colour rather than equal to it: a thin line's edge is softened into what lies under it.
    public bool Is(int x, int y, Rgba colour)
    {
        const int near = 60;
        var at = ((y * Width) + x) * 4;
        return Math.Abs(_pixels[at] - colour.R) <= near && Math.Abs(_pixels[at + 1] - colour.G) <= near
               && Math.Abs(_pixels[at + 2] - colour.B) <= near && _pixels[at + 3] >= 128;
    }

    public bool AnyIn(int left, int top, int right, int bottom, Rgba colour) =>
        Enumerable.Range(top, bottom - top).Any(y => Enumerable.Range(left, right - left).Any(x => Is(x, y, colour)));
}