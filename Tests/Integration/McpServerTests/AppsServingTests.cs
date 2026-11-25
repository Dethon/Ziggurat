using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.DTOs.WebChat;
using Domain.Security;
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
        var unknownHost = $"glucose-zzzzzz.{AppsServer.AppsDomain}";
        var unknown = await server.GetAsync(unknownHost, "/", server.SessionFor(AppsServer.Her, unknownHost), navigate: true);

        HttpResponseMessage[] refused =
        [
            await server.GetAsync(published, "/", server.SessionFor(AppsServer.Him, published), navigate: true),
            await server.GetAsync(published, "/", server.SessionFor(AppsServer.Neighbour, published), navigate: true),
            // Nobody, asking for anything but a page (a page is sent to sign in: AppsSignInTests).
            await server.GetAsync(published, "/"),
            await server.GetAsync(published, "/", "no-such-session"),
            // Her session on the preview is not one on the published origin, nor the other way round (a page
            // opened so is sent to sign in for that origin).
            await server.GetAsync(published, "/", server.SessionFor(AppsServer.Her, preview)),
            await server.GetAsync(preview, "/", server.SessionFor(AppsServer.Her, published)),
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

    [Fact]
    public async Task AnExpiredSession_OpensNothing()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        var expired = server.Sessions.Open(AppsServer.Her, "accounts-session-1", AppOrigin.Parse(host, AppsServer.AppsDomain)!,
            TestAssertions.Of(AppsServer.Her, AppChannels.Apps, sessionId: "accounts-session-1"), DateTimeOffset.UtcNow.AddSeconds(-1));

        (await server.GetAsync(host, "/", expired)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
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

    // Every answer on an app origin carries the policy its manifest makes: the published origin the
    // served commit's manifest, the preview the tree's.
    [Fact]
    public async Task EveryAnswer_CarriesThePolicyItsOriginsManifestMakes()
    {
        await using var server = await AppsServer.StartAsync();
        var created = await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose","accentColor":"#e9601f","description":"Mine.","connect":["https://api.example.org"]}""");
        var id = AppsServer.IdIn(created["note"]!.GetValue<string>());
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Hi</h1>");
        await server.CreateAsync(AppsServer.Her, "/glucose/app.js", "console.log(1)");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose","accentColor":"#e9601f","description":"Mine.","connect":["https://tiles.example.net"]}""", overwrite: true);
        var published = server.Published("glucose", id);
        var preview = server.Preview("glucose", id);

        foreach (var path in new[] { "/", "/app.js", "/missing.js", "/_app/manifest.webmanifest", "/_app/sw.js", "/_app/icon.svg", "/_app/apps.js" })
        {
            var fromPublished = Policy(await server.GetAsync(published, path, server.SessionFor(AppsServer.Her, published)));
            var fromPreview = Policy(await server.GetAsync(preview, path, server.SessionFor(AppsServer.Her, preview)));

            fromPublished.ShouldStartWith("default-src 'none'; ", customMessage: path);
            fromPublished.ShouldContain("script-src 'self';", customMessage: path);
            fromPublished.ShouldContain("connect-src 'self' https://api.example.org;", customMessage: path);
            fromPublished.ShouldContain("frame-ancestors 'none'", customMessage: path);
            fromPreview.ShouldContain("connect-src 'self' https://tiles.example.net;", customMessage: path);
        }
    }

    // The outside code an origin's pages may load is what its pages were found pinned with: the published
    // origin's as its commit's publish recorded it (a rollback serves the earlier commit's own), the
    // preview's as a publish would find it in the working tree now.
    [Fact]
    public async Task AnOriginsPolicy_LoadsTheCodeItsOwnPagesPinned_AndARollbackServesItsCommitsOwn()
    {
        const string hash = "sha384-oqVuAfXRKap7fdgcCY5uykM6+R9GqQ8K/uxy9rx7HNQlGYl1kPzQho1wx4JwY8wC";
        static string page(string package) => $"""<script src="https://cdn.jsdelivr.net/npm/{package}/dist/x.js" integrity="{hash}"></script>""";
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", page("chart.js@4.4.1"));
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", page("d3@7.8.5"), overwrite: true);
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", page("preact@10.19.3"), overwrite: true);
        var published = server.Published("glucose", id);
        var preview = server.Preview("glucose", id);
        async Task<string> scriptsAsync(string host) =>
            Policy(await server.GetAsync(host, "/", server.SessionFor(AppsServer.Her, host))).Split("; ").Single(d => d.StartsWith("script-src "));

        (await scriptsAsync(published)).ShouldBe("script-src 'self' https://cdn.jsdelivr.net/npm/d3@7.8.5/");
        (await scriptsAsync(preview)).ShouldBe("script-src 'self' https://cdn.jsdelivr.net/npm/preact@10.19.3/");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./rollback HEAD~1");
        (await scriptsAsync(published)).ShouldBe("script-src 'self' https://cdn.jsdelivr.net/npm/chart.js@4.4.1/");
    }

    // A page gets the app's web manifest, its service worker and the client library, whatever the
    // agent wrote: as files on the origin, since the policy runs no inline script. Nothing else is touched.
    [Theory]
    [InlineData("<!doctype html><html lang=\"en\"><HEAD data-x=\"1\"><title>Hi</title></HEAD><body>Hi</body></html>",
        "<!doctype html><html lang=\"en\"><HEAD data-x=\"1\">{0}<title>Hi</title></HEAD><body>Hi</body></html>")]
    [InlineData("<!doctype html><html><body>Hi</body></html>", "<!doctype html><html>{0}<body>Hi</body></html>")]
    [InlineData("<!DOCTYPE html><h1>Hi</h1>", "<!DOCTYPE html>{0}<h1>Hi</h1>")]
    [InlineData("<h1>Hi</h1>", "{0}<h1>Hi</h1>")]
    public async Task APage_HasTheManifestTheWorkerAndTheClientLibraryAdded(string written, string served)
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/week/index.html", written);
        var host = server.Preview("glucose", id);

        var page = await server.GetAsync(host, "/week/", server.SessionFor(AppsServer.Her, host));

        page.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await page.Content.ReadAsStringAsync()).ShouldBe(string.Format(served,
            "<link rel=\"manifest\" href=\"/_app/manifest.webmanifest\" crossorigin=\"use-credentials\">"
            + "<script src=\"/_app/apps.js\"></script>"
            + "<script src=\"/_app/register.js\" defer></script>"));
    }

    [Fact]
    public async Task AnythingButAPage_IsServedAsWritten()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/head.js", "const html = '<head></head>';");
        var host = server.Preview("glucose", id);

        var script = await server.GetAsync(host, "/head.js", server.SessionFor(AppsServer.Her, host));

        (await script.Content.ReadAsStringAsync()).ShouldBe("const html = '<head></head>';");
        script.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
    }

    // Each origin installs as an app of its own: its name, its colour, its icon drawn in that colour,
    // starting and scoped at its root.
    [Fact]
    public async Task TheWebManifest_IsTheAppsNameAndColour_StartingAtTheRoot()
    {
        await using var server = await AppsServer.StartAsync();
        var created = await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose today","shortName":"Glucose","accentColor":"#3a7d44","description":"Mine."}""");
        var id = AppsServer.IdIn(created["note"]!.GetValue<string>());
        var host = server.Preview("glucose", id);
        var session = server.SessionFor(AppsServer.Her, host);

        var response = await server.GetAsync(host, "/_app/manifest.webmanifest", session);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/manifest+json");
        var manifest = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        manifest["name"]!.GetValue<string>().ShouldBe("Glucose today");
        manifest["short_name"]!.GetValue<string>().ShouldBe("Glucose");
        manifest["start_url"]!.GetValue<string>().ShouldBe("/");
        manifest["scope"]!.GetValue<string>().ShouldBe("/");
        manifest["id"]!.GetValue<string>().ShouldBe("/");
        manifest["display"]!.GetValue<string>().ShouldBe("standalone");
        manifest["theme_color"]!.GetValue<string>().ShouldBe("#3a7d44");
        var icon = manifest["icons"]!.AsArray().ShouldHaveSingleItem()!;
        icon["src"]!.GetValue<string>().ShouldBe("/_app/icon.svg");
        icon["type"]!.GetValue<string>().ShouldBe("image/svg+xml");

        var drawn = await server.GetAsync(host, "/_app/icon.svg", session);
        drawn.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
        (await drawn.Content.ReadAsStringAsync()).ShouldBe(SpaceIcon.Svg("#3a7d44"));
    }

    // An app that names its own SVG installs with that drawing, the badge in its corner, read from what the
    // origin serves (here the preview's working tree): one whose file is not there yet keeps the colour's.
    [Fact]
    public async Task AnAppsOwnIcon_IsWhatItInstallsWith_BadgedLikeEveryAppsIcon()
    {
        await using var server = await AppsServer.StartAsync();
        const string drawing = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"12\" r=\"10\" fill=\"#c2410c\"/></svg>";
        var created = await server.CreateAsync(AppsServer.Her, "/casa/app.json",
            """{"name":"Casa","accentColor":"#c2410c","description":"Mine.","root":"dist","icon":"/icons/casa.svg"}""");
        var id = AppsServer.IdIn(created["note"]!.GetValue<string>());
        var host = server.Preview("casa", id);
        var session = server.SessionFor(AppsServer.Her, host);
        var before = await (await server.GetAsync(host, "/_app/icon.svg", session)).Content.ReadAsStringAsync();
        await server.CreateAsync(AppsServer.Her, "/casa/dist/icons/casa.svg", drawing);

        var drawn = await server.GetAsync(host, "/_app/icon.svg", session);

        before.ShouldBe(SpaceIcon.Svg("#c2410c"));
        drawn.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
        (await drawn.Content.ReadAsStringAsync()).ShouldBe(SpaceIcon.Badged(Encoding.UTF8.GetBytes(drawing)));
    }

    [Fact]
    public async Task AnAppWithNoShortName_IsShownByItsName()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose", name: "Glucose today"));
        var host = server.Preview("glucose", id);

        var manifest = JsonNode.Parse(await (await server.GetAsync(host, "/_app/manifest.webmanifest",
            server.SessionFor(AppsServer.Her, host))).Content.ReadAsStringAsync())!;

        manifest["short_name"]!.GetValue<string>().ShouldBe("Glucose today");
    }

    // The worker is the origin's whole scope, revalidated on every check so a new one is found, and
    // keeps the app's own files for offline — never a read of the household's data.
    [Fact]
    public async Task TheServiceWorker_IsScopedAtTheRoot_AndNeverKeepsAVfsAnswer()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        var host = server.Preview("glucose", id);
        var session = server.SessionFor(AppsServer.Her, host);

        var worker = await server.GetAsync(host, "/_app/sw.js", session);
        var registration = await server.GetAsync(host, "/_app/register.js", session);
        var library = await server.GetAsync(host, "/_app/apps.js", session);

        worker.StatusCode.ShouldBe(HttpStatusCode.OK);
        worker.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        worker.Headers.GetValues("Service-Worker-Allowed").ShouldBe(["/"]);
        worker.Headers.CacheControl!.NoCache.ShouldBeTrue();
        var script = await worker.Content.ReadAsStringAsync();
        script.ShouldContain("caches.open");
        script.ShouldContain("'/_vfs/'");
        (await registration.Content.ReadAsStringAsync()).ShouldContain("navigator.serviceWorker.register('/_app/sw.js', { scope: '/' })");
        library.StatusCode.ShouldBe(HttpStatusCode.OK);
        library.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
    }

    // The server's own files are not the agent's to replace: a file it wrote under /_app/ is never served.
    [Fact]
    public async Task TheServersOwnFiles_WinOverTheAppsFilesOfTheSameName()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/_app/sw.js", "self.registration.unregister()");
        await server.CreateAsync(AppsServer.Her, "/glucose/_app/mine.js", "mine");
        var host = server.Preview("glucose", id);
        var session = server.SessionFor(AppsServer.Her, host);

        (await (await server.GetAsync(host, "/_app/sw.js", session)).Content.ReadAsStringAsync()).ShouldContain("caches.open");
        (await server.GetAsync(host, "/_app/mine.js", session)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A removed app's origin still says what happened to whoever opens it, an installed copy included,
    // with no session asked: its pages are a short page saying so, and its worker one that unregisters
    // itself and forgets what it kept, so the installed app stops opening a copy of something gone.
    [Fact]
    public async Task ARemovedAppsOrigin_SaysSo_AndItsWorkerUnregistersItself()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        await server.CallAsync("fs_delete", new Dictionary<string, object?> { ["path"] = "/glucose" }, AppsServer.TurnOf(AppsServer.Her));

        foreach (var host in new[] { server.Published("glucose", id), server.Preview("glucose", id) })
        {
            var page = await server.GetAsync(host, "/week/", navigate: true);
            page.StatusCode.ShouldBe(HttpStatusCode.Gone, host);
            page.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
            (await page.Content.ReadAsStringAsync()).ShouldContain("This app was removed");
            Policy(page).ShouldBe(AppContentPolicy.Closed);

            var worker = await server.GetAsync(host, "/_app/sw.js");
            worker.StatusCode.ShouldBe(HttpStatusCode.OK, host);
            worker.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
            worker.Headers.GetValues("Service-Worker-Allowed").ShouldBe(["/"]);
            var script = await worker.Content.ReadAsStringAsync();
            script.ShouldContain("self.registration.unregister()");
            script.ShouldContain("caches.delete");

            (await server.GetAsync(host, "/app.js")).StatusCode.ShouldBe(HttpStatusCode.NotFound, host);
        }
    }

    // An id is never handed to a second app: its origin keeps what the first left in browsers.
    [Fact]
    public async Task ARemovedAppsId_IsNeverAnotherApps()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        await server.CallAsync("fs_delete", new Dictionary<string, object?> { ["path"] = "/glucose" }, AppsServer.TurnOf(AppsServer.Her));

        var store = server.Store;
        store.IsRemoved(id).ShouldBeTrue();
        store.IdTaken(id).ShouldBeTrue();
        (await server.GlobAsync(AppsServer.Her, "*")).ShouldBeEmpty();
    }

    private static string Policy(HttpResponseMessage response) =>
        response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();

    private static async Task<string> Answer(HttpResponseMessage response) =>
        $"{(int)response.StatusCode} {response.Content.Headers.ContentType} "
        + string.Join("|", response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key is not ("Date" or "Content-Length"))
            .OrderBy(h => h.Key)
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}"))
        + " " + await response.Content.ReadAsStringAsync();
}