using System.Xml;
using Domain.DTOs.WebChat;

namespace Domain.Apps;

// A built app's own home screen icon: an SVG its origin serves, named in its manifest, shown with the
// badge every app's icon carries (SpaceIcon). An SVG alone: one drawing fits every size a launcher asks.
public static class AppIcon
{
    public const int MaxBytes = 256 * 1024;

    // What the manifest may name: a path the origin serves, ending in .svg, never the server's /_app/.
    public static bool IsServedPath(string path) =>
        path.Length is > 1 and <= 200
        && path.StartsWith('/')
        && path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
        && path.IndexOfAny(['\\', '?', '#']) < 0
        && path[1..].Split('/') is var segments
        && segments[0] != "_app"
        && segments.All(segment => segment.Length > 0 && segment is not ("." or ".." or ".git" or "node_modules"));

    // The file under the served root the path names.
    public static string Relative(string path) => path[1..];

    // A whole, well-formed <svg> document of the SVG namespace, small enough to hand a launcher. No DTD
    // is read, so nothing it names is fetched or expanded.
    public static bool IsDrawing(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxBytes)
        {
            return false;
        }

        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            reader.MoveToContent();
            var drawing = reader is { NodeType: XmlNodeType.Element, LocalName: "svg", NamespaceURI: "http://www.w3.org/2000/svg" };
            while (reader.Read())
            {
            }

            return drawing;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    // What /_app/icon.svg serves: the app's drawing with the badge when it names one the origin serves,
    // else its accent colour's.
    public static string Svg(AppManifest manifest, byte[]? drawing) =>
        manifest.Icon is not null && drawing is not null && IsDrawing(drawing)
            ? SpaceIcon.Badged(drawing)
            : SpaceIcon.Svg(manifest.AccentColor);
}