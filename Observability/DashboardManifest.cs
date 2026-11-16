namespace Observability;

public record ManifestIcon(string Src, string Sizes, string Type);

public static class DashboardManifest
{
    // PNGs first: Android Chrome installs from a raster icon, and offered only the SVG its install
    // dialog showed no icon and its button did nothing. The SVG stays for the browsers that draw it.
    public static readonly IReadOnlyList<ManifestIcon> Icons =
    [
        new("icon-192.png", "192x192", "image/png"),
        new("icon-512.png", "512x512", "image/png"),
        new("icon.svg", "any", "image/svg+xml")
    ];

    public static object Build() => new
    {
        name = "Ziggurat Dashboard",
        short_name = "Dashboard",
        start_url = "/",
        id = "/",
        scope = "/",
        display = "standalone",
        background_color = "#0b0f17",
        theme_color = "#0b0f17",
        prefer_related_applications = false,
        icons = Icons.Select(i => new { src = i.Src, sizes = i.Sizes, type = i.Type })
    };
}