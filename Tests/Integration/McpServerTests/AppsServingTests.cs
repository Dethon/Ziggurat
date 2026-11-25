using System.Net;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A built app as a browser opens it: the proxy hands every request for `*.<apps domain>` to the
// apps server's pages listener, which reads the app from the Host and the viewer from the app
// session its own cookie names. The published origin serves the served commit, the preview the
// working tree; everyone else, and every label that is no app, gets one and the same 404.
public class AppsServingTests
{
    private static async Task<(AppsServer Server, string Id)> PublishedAsync(string page = "<h1>Published</h1>")
    {
        var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", page);
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        return (server, id);
    }

    [Fact]
    public async Task ThePublishedOrigin_ServesTheServedCommit_ToItsAuthor()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Still being written</h1>", overwrite: true);

        var page = await server.GetAsync(host, "/", server.SessionFor(AppsServer.Her, host), navigate: true);

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.ShouldContain("<h1>Published</h1>");
        html.ShouldNotContain("Still being written");
    }

    [Fact]
    public async Task ThePreviewOrigin_ServesTheWorkingTree_ToItsAuthor()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Preview("glucose", id);
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Still being written</h1>", overwrite: true);

        var page = await server.GetAsync(host, "/index.html", server.SessionFor(AppsServer.Her, host));

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<h1>Still being written</h1>");
    }

    // The preview needs no publish: it is the tree the agent is writing.
    [Fact]
    public async Task AnAppNeverPublished_HasAPreview_ButNoPublishedApp()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Draft</h1>");
        var preview = server.Preview("glucose", id);
        var published = server.Published("glucose", id);

        (await server.GetAsync(preview, "/", server.SessionFor(AppsServer.Her, preview))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await server.GetAsync(published, "/", server.SessionFor(AppsServer.Her, published))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    // Rolling back moves what the published origin serves, and nothing else.
    [Fact]
    public async Task ARollback_ServesTheEarlierCommitsFiles()
    {
        var (server, id) = await PublishedAsync("<h1>One</h1>");
        await using var _ = server;
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Two</h1>", overwrite: true);
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        var history = (await server.ExecAsync(AppsServer.Her, "/glucose", "./history"))["stdout"]!.GetValue<string>();
        var first = history.Split('\n')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        await server.ExecAsync(AppsServer.Her, "/glucose", $"./rollback {first}");
        var host = server.Published("glucose", id);

        var page = await server.GetAsync(host, "/", server.SessionFor(AppsServer.Her, host));

        (await page.Content.ReadAsStringAsync()).ShouldContain("<h1>One</h1>");
    }

    // A build's output is bytes: an image or a font is served as written, through git and not.
    [Fact]
    public async Task ABinaryFile_IsServedAsItsBytes_FromBothOrigins()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xFE, 0x80, 0x0D, 0x0A, 0xC3];
        await server.CallAsync("fs_blob_write", new Dictionary<string, object?>
        {
            ["path"] = "/glucose/img/logo.png", ["contentBase64"] = Convert.ToBase64String(png), ["offset"] = 0,
            ["overwrite"] = false, ["createDirectories"] = true
        }, AppsServer.TurnOf(AppsServer.Her));
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");

        foreach (var host in new[] { server.Published("glucose", id), server.Preview("glucose", id) })
        {
            var image = await server.GetAsync(host, "/img/logo.png", server.SessionFor(AppsServer.Her, host));

            image.StatusCode.ShouldBe(HttpStatusCode.OK, host);
            (await image.Content.ReadAsByteArrayAsync()).ShouldBe(png, host);
            image.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        }
    }

    // The manifest's root is what is served: the rest of the repository (sources, the manifest) is
    // the agent's, never a page's.
    [Fact]
    public async Task OnlyTheManifestsRoot_IsServed()
    {
        await using var server = await AppsServer.StartAsync();
        var created = await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose","accentColor":"#e9601f","description":"Mine.","root":"dist"}""");
        var id = AppsServer.IdIn(created["note"]!.GetValue<string>());
        await server.CreateAsync(AppsServer.Her, "/glucose/dist/index.html", "<h1>Built</h1>");
        await server.CreateAsync(AppsServer.Her, "/glucose/src/main.ts", "export {}");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");

        foreach (var host in new[] { server.Published("glucose", id), server.Preview("glucose", id) })
        {
            var session = server.SessionFor(AppsServer.Her, host);
            (await (await server.GetAsync(host, "/", session)).Content.ReadAsStringAsync()).ShouldContain("<h1>Built</h1>", customMessage: host);
            (await server.GetAsync(host, "/src/main.ts", session)).StatusCode.ShouldBe(HttpStatusCode.NotFound, host);
            (await server.GetAsync(host, "/app.json", session)).StatusCode.ShouldBe(HttpStatusCode.NotFound, host);
            (await server.GetAsync(host, "/../app.json", session)).StatusCode.ShouldBe(HttpStatusCode.NotFound, host);
            (await server.GetAsync(host, "/%2e%2e/app.json", session)).StatusCode.ShouldBe(HttpStatusCode.NotFound, host);
        }
    }

    [Fact]
    public async Task ADirectory_IsServedItsIndex_AndAMissingPageOpensTheAppsOwnIndex()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Home</h1>");
        await server.CreateAsync(AppsServer.Her, "/glucose/week/index.html", "<h1>Week</h1>");
        var host = server.Preview("glucose", id);
        var session = server.SessionFor(AppsServer.Her, host);

        (await (await server.GetAsync(host, "/week/", session)).Content.ReadAsStringAsync()).ShouldContain("<h1>Week</h1>");
        var bare = await server.GetAsync(host, "/week?day=3", session);
        bare.StatusCode.ShouldBe(HttpStatusCode.PermanentRedirect);
        bare.Headers.Location!.OriginalString.ShouldBe("/week/?day=3");
        // A client-side route: the app's own index answers a page being opened, never a missing file.
        (await (await server.GetAsync(host, "/day/2026-11-20", session, navigate: true)).Content.ReadAsStringAsync())
            .ShouldContain("<h1>Home</h1>");
        (await server.GetAsync(host, "/missing.js", session)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The id alone finds the app: a link made before a rename still opens it.
    [Fact]
    public async Task ARenamedAppsOldLink_StillOpensIt()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        await server.CallAsync("fs_move", new Dictionary<string, object?> { ["sourcePath"] = "/glucose", ["destinationPath"] = "/sugar" },
            AppsServer.TurnOf(AppsServer.Her));
        var oldHost = server.Published("glucose", id);

        var page = await server.GetAsync(oldHost, "/", server.SessionFor(AppsServer.Her, oldHost));

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Who is not let in learns nothing: the answer for another member, another household, nobody, a
    // session for the other origin, an app that is not there and a label that is no app's is one.
    [Fact]
    public async Task EveryoneButTheAuthor_GetsTheSame404AsAnUnknownApp()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var published = server.Published("glucose", id);
        var preview = server.Preview("glucose", id);
        var unknown = await server.GetAsync($"glucose-zzzzzz.{AppsServer.AppsDomain}", "/", navigate: true);

        HttpResponseMessage[] refused =
        [
            await server.GetAsync(published, "/", server.SessionFor(AppsServer.Him, published), navigate: true),
            await server.GetAsync(published, "/", server.SessionFor(AppsServer.Neighbour, published), navigate: true),
            await server.GetAsync(published, "/", navigate: true),
            await server.GetAsync(published, "/", "no-such-session", navigate: true),
            // Her session on the preview is not one on the published origin, nor the other way round.
            await server.GetAsync(published, "/", server.SessionFor(AppsServer.Her, preview), navigate: true),
            await server.GetAsync(preview, "/", server.SessionFor(AppsServer.Her, published), navigate: true),
            await server.GetAsync(preview, "/", server.SessionFor(AppsServer.Him, preview), navigate: true),
            await server.GetAsync($"glucose.{AppsServer.AppsDomain}", "/", navigate: true),
            await server.GetAsync("localhost", "/", navigate: true)
        ];

        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var expected = await Answer(unknown);
        foreach (var response in refused)
        {
            (await Answer(response)).ShouldBe(expected);
        }
    }

    // An ended or expired session opens nothing.
    [Fact]
    public async Task ASessionThatEnded_OpensNothing()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        var session = server.SessionFor(AppsServer.Her, host);
        (await server.GetAsync(host, "/", session)).StatusCode.ShouldBe(HttpStatusCode.OK);

        server.Sessions.EndAll(AppsServer.Her).ShouldBe(1);

        (await server.GetAsync(host, "/", session)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The pages listener answers app origins and nothing else: never the deployment's /mcp.
    [Fact]
    public async Task ThePagesListener_ServesNoMcp()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);

        (await server.GetAsync(host, "/mcp", server.SessionFor(AppsServer.Her, host))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await server.GetAsync("localhost", "/mcp")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<string> Answer(HttpResponseMessage response) =>
        $"{(int)response.StatusCode} {response.Content.Headers.ContentType} "
        + string.Join("|", response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key is not ("Date" or "Content-Length"))
            .OrderBy(h => h.Key)
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}"))
        + " " + await response.Content.ReadAsStringAsync();
}