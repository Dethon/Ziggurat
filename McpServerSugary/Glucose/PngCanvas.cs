using System.Buffers.Binary;
using System.IO.Compression;

namespace McpServerSugary.Glucose;

public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Hex(string hex, byte alpha = 255) => new(
        Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16), alpha);
}

// A picture on a transparent ground, encoded with nothing but the framework's deflate, since all the
// widget's images need is lines and fills. Loops, not LINQ: this is per pixel.
public sealed class PngCanvas(int width, int height)
{
    private readonly byte[] _pixels = new byte[width * height * 4];

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] _crcTable = [.. Enumerable.Range(0, 256).Select(n =>
        Enumerable.Range(0, 8).Aggregate((uint)n, (c, _) => (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1))];

    public int Width => width;

    public int Height => height;

    public void Row(int left, int right, int y, Rgba colour)
    {
        if (y < 0 || y >= height)
        {
            return;
        }

        for (var x = Math.Max(0, left); x <= Math.Min(width - 1, right); x++)
        {
            Blend((y * width * 4) + (x * 4), colour, 1);
        }
    }

    // The whole picture, its corners rounded and softened as a line's edge is.
    public void FillRounded(double corner, Rgba colour)
    {
        var (innerRight, innerBottom) = (width - 1 - corner, height - 1 - corner);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (dx, dy) = (Math.Max(Math.Max(corner - x, x - innerRight), 0), Math.Max(Math.Max(corner - y, y - innerBottom), 0));
                var coverage = Math.Clamp(corner + 0.5 - Math.Sqrt((dx * dx) + (dy * dy)), 0, 1);
                if (coverage > 0)
                {
                    Blend((y * width * 4) + (x * 4), colour, coverage);
                }
            }
        }
    }

    // A round-ended line, its edge softened by how far each pixel's centre is from it.
    public void Line(double x0, double y0, double x1, double y1, double radius, Rgba colour)
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
                    Blend((y * width * 4) + (x * 4), colour, coverage);
                }
            }
        }
    }

    public byte[] ToPng()
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var row = 0; row < height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write(_pixels, row * width * 4, width * 4);
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

    private void Blend(int at, Rgba colour, double coverage)
    {
        var over = colour.A / 255.0 * coverage;
        var under = _pixels[at + 3] / 255.0;
        var alpha = over + (under * (1 - over));
        if (alpha <= 0)
        {
            return;
        }

        byte Mix(byte top, byte bottom) => (byte)Math.Round(((top * over) + (bottom * under * (1 - over))) / alpha);

        _pixels[at] = Mix(colour.R, _pixels[at]);
        _pixels[at + 1] = Mix(colour.G, _pixels[at + 1]);
        _pixels[at + 2] = Mix(colour.B, _pixels[at + 2]);
        _pixels[at + 3] = (byte)Math.Round(alpha * 255);
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