using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The app itself, served by the Sugary server from its web root: the built PWA in the image, a
// stand-in here so the test needs no node build.
public sealed class SugaryAppServingTests : IDisposable
{
    private readonly string _webRoot = Directory.CreateTempSubdirectory("sugary-web-").FullName;

    public SugaryAppServingTests()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>Sugary</title>");
        File.WriteAllText(Path.Combine(_webRoot, "sw.js"), "self.addEventListener('install', () => {});");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "index-1a2b3c.js"), "console.log('sugary');");
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    // The shell loads before any code is typed: it is the app that asks for the code.
    [Theory]
    [InlineData("/")]
    [InlineData("/log")]
    public async Task TheApp_LoadsFromTheServer(string path)
    {
        await using var server = await SugaryServer.StartAsync(webRoot: _webRoot);

        var response = await server.Browser().GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<title>Sugary</title>");
    }

    // Vite fingerprints everything under assets/; the rest (index.html, the service worker, the
    // manifest) must be revalidated or a phone keeps running an old build.
    [Theory]
    [InlineData("/", "no-cache")]
    [InlineData("/sw.js", "no-cache")]
    [InlineData("/assets/index-1a2b3c.js", "public, max-age=31536000, immutable")]
    public async Task OnlyFingerprintedFiles_AreCachedForGood(string path, string cacheControl)
    {
        await using var server = await SugaryServer.StartAsync(webRoot: _webRoot);

        var response = await server.Browser().GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().ShouldBe(cacheControl);
    }

    [Fact]
    public async Task AMissingAsset_IsNotAnsweredWithTheApp()
    {
        await using var server = await SugaryServer.StartAsync(webRoot: _webRoot);

        (await server.Browser().GetAsync("/assets/gone-000000.js")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Estimating a meal and reading a label are turns of the sugary agent, which this server does
    // not answer yet. The app is told so up front and shows them as not available; an installed
    // build that asks anyway gets a sentence, not a failure it cannot explain.
    [Fact]
    public async Task EstimatesAndLabels_AreNotAvailableYet()
    {
        await using var server = await SugaryServer.StartAsync(webRoot: _webRoot);
        var client = await server.SignedInAsync();

        var session = (await client.GetFromJsonAsync<JsonNode>("/api/session"))!;
        session["estimates"]!.GetValue<bool>().ShouldBeFalse();
        session["labels"]!.GetValue<bool>().ShouldBeFalse();

        foreach (var path in new[] { "/api/estimates", "/api/labels" })
        {
            var response = await client.PostAsync(path, new MultipartFormDataContent());
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, path);
            (await response.Content.ReadFromJsonAsync<JsonNode>())!["detail"]!.GetValue<string>()
                .ShouldContain("not available yet");
        }
    }
}