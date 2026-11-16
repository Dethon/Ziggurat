using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Observability;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Observability;

// An installed dashboard is only as good as the manifest a phone can read. The browser fetches it
// as no page is opened (Sec-Fetch-Mode cors, Accept */*), so the proxy hands it on unchecked and
// with no assertion, and the gate admits that only for a file of the web root: served from a route,
// it was 401 even to the signed-in operator, and Android answered "this app cannot be installed".
public class DashboardManifestTests
{
    private static readonly string _webRoot =
        Path.Combine(TestHelpers.FindSolutionRoot(), "Dashboard.Client", "wwwroot");

    [Fact]
    public async Task TheManifest_IsServedToTheBrowsersOwnFetch_ThroughTheGate()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = _webRoot });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(TestAssertions.Verifier());
        await using var app = builder.Build();
        app.UseOperatorGate();
        app.UseStaticFiles();
        await app.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/manifest.webmanifest");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        using var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/manifest+json");
    }

    [Theory]
    [InlineData(192)]
    [InlineData(512)]
    public void Manifest_OffersAPngIconAtTheSizeAndroidInstallsFrom(int size)
    {
        Icons().ShouldContain(
            i => i.Type == "image/png" && i.Sizes == $"{size}x{size}",
            $"no {size}x{size} PNG in the dashboard's manifest");
    }

    [Fact]
    public void EveryPngIcon_IsAFileOfTheWebRootAtTheSizeItClaims()
    {
        foreach (var icon in Icons().Where(i => i.Type == "image/png"))
        {
            var file = Path.Combine(_webRoot, icon.Src);
            File.Exists(file).ShouldBeTrue($"{icon.Src} is not in Dashboard.Client/wwwroot");

            var header = File.ReadAllBytes(file).AsSpan(0, 24);
            var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            $"{width}x{height}".ShouldBe(icon.Sizes, $"{icon.Src} is not the size the manifest says");
        }
    }

    private sealed record Icon(string Src, string Sizes, string Type);

    private static IReadOnlyList<Icon> Icons()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_webRoot, "manifest.webmanifest")));
        return manifest.RootElement.GetProperty("icons").EnumerateArray()
            .Select(i => new Icon(
                i.GetProperty("src").GetString()!,
                i.GetProperty("sizes").GetString()!,
                i.GetProperty("type").GetString()!))
            .ToList();
    }
}