using Domain.Security;
using Observability;
using Observability.Hubs;
using Observability.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>();

var redisConnection = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Redis:ConnectionString is required");

builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnection));

builder.Services.AddSignalR();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MetricsQueryService>();
builder.Services.AddHostedService<MetricsCollectorService>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<HttpHealthProbeService>();
// Where the accounts service publishes the keys its assertions verify with (ACCOUNTS__JWKSURL).
builder.Services.AddOperatorGate(builder.Configuration["Accounts:JwksUrl"] is { Length: > 0 } jwksUrl
    ? jwksUrl
    : AssertionVerifier.DefaultJwksUrl);

if (!builder.Environment.IsProduction())
{
    builder.WebHost.UseStaticWebAssets();
}

var app = builder.Build();

app.UseOperatorGate();
app.UseRouting();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGet("/manifest.webmanifest", () =>
{
    var manifest = new
    {
        name = "Ziggurat Dashboard",
        short_name = "Dashboard",
        start_url = "/",
        id = "/",
        scope = "/",
        display = "standalone",
        background_color = "#1a1a2e",
        theme_color = "#1a1a2e",
        prefer_related_applications = false,
        icons = new[]
        {
            new { src = "favicon.svg", sizes = "any", type = "image/svg+xml" }
        }
    };
    return Results.Json(manifest, contentType: "application/manifest+json");
});

app.MapHub<MetricsHub>("/hubs/metrics");
app.MapMetricsApi();

app.MapFallbackToFile("index.html");

app.Run();