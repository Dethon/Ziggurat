using Domain.DTOs.WebChat;
using JetBrains.Annotations;
using WebChat;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);

if (!builder.Environment.IsProduction())
{
    builder.WebHost.UseStaticWebAssets();
}

var app = builder.Build();
app.UseBlazorFrameworkFiles();

// The household's spaces are the hub's to say, never this host's, which reads no header and knows
// nobody: the page that learned its space names it here (manifestHelper), and what it names is held
// to the shapes a space's configuration takes. A page with nothing to name gets the default space's.
app.MapGet("/manifest.webmanifest", (string? slug, string? name, string? color) =>
{
    // Longer than any space's name a person would install under; a longer one is no name here.
    const int maxName = 100;
    var spaceSlug = SpaceConfig.IsValidSlug(slug) ? slug! : SpaceConfig.DefaultSlug;
    var accent = SpaceConfig.IsValidHexColor(color) ? color : null;
    // Before the page has heard from the hub it knows its slug alone: the app is still that space's
    // (its start, id and scope), under the deployment's name until the page names it.
    var named = name is { Length: > 0 and <= maxName }
        ? new SpaceConfig(spaceSlug, name, accent ?? SpaceConfig.DefaultAccentColor)
        : null;
    var startUrl = spaceSlug == SpaceConfig.DefaultSlug ? "/" : $"/{spaceSlug}";
    var (manifestName, shortName) = WebManifestNaming.Resolve(named);
    var themeColor = accent ?? "#1a1a2e";

    var iconColor = Uri.EscapeDataString(accent ?? SpaceConfig.DefaultAccentColor);
    var manifest = new
    {
        name = manifestName,
        short_name = shortName,
        start_url = startUrl,
        id = startUrl,
        scope = startUrl,
        display = "standalone",
        background_color = "#1a1a2e",
        theme_color = themeColor,
        prefer_related_applications = false,
        icons = new[] { new { src = $"/icon.svg?color={iconColor}", sizes = "any", type = "image/svg+xml" } }
    };

    return Results.Json(manifest, contentType: "application/manifest+json");
});

app.MapGet("/icon.svg", (string? color) =>
{
    var fill = SpaceConfig.IsValidHexColor(color) ? color! : SpaceConfig.DefaultAccentColor;
    var svg = $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 50" width="120" height="50">
            <text x="60" y="38" text-anchor="middle" font-family="Arial, sans-serif" font-size="40" fill="{fill}">ᓚᘏᗢ</text>
        </svg>
        """;
    return Results.Text(svg, "image/svg+xml");
});

app.UseStaticFiles();

// Who is signed in is the accounts service's to say, never this configuration's.
app.MapGet("/api/config", (IConfiguration config) => new AppConfig(
    config["AgentUrl"] ?? throw new InvalidOperationException("AgentUrl is not configured"),
    config["WebPush:PublicKey"]));

app.MapFallbackToFile("index.html");

await app.RunAsync();

namespace WebChat
{

    [UsedImplicitly]
    internal record AppConfig(string AgentUrl, string? VapidPublicKey);
}