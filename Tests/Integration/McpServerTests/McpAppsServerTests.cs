using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.DTOs.Channel;
using Domain.Tools;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The /apps mount over MCP, as the agent's file tools drive it on a person's turn: who sees which
// apps, creating one by its manifest, what may be written into it, and deleting it.
public class McpAppsServerTests
{
    [Fact]
    public async Task ACallActingForNobody_IsRefused_NeverShownAnEmptyList()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        var guest = new ConversationContext("jonas", "c", "guest", new ReplyTarget("voice", "c"),
            Principal: new Principal("voice", "guest"));

        foreach (var turn in new ConversationContext?[] { null, guest })
        {
            var refused = await server.CallAsync("fs_glob", new Dictionary<string, object?> { ["pattern"] = "**" }, turn);

            refused["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied, refused.ToJsonString());
        }
    }

    [Fact]
    public async Task WritingTheManifest_CreatesTheApp_ListedForItsAuthorAlone()
    {
        await using var server = await AppsServer.StartAsync();

        var note = await server.AppAsync(AppsServer.Her, "glucose");

        Regex.IsMatch(note, @"https://glucose-[a-z0-9]{6}\.apps\.test/").ShouldBeTrue(note);
        (await server.GlobAsync(AppsServer.Her, "*")).ShouldBe(["/glucose/"]);
        (await server.ReadAsync(AppsServer.Her, "/glucose/app.json")).ShouldContain("Glucose today");
        (await server.GlobAsync(AppsServer.Him, "**")).ShouldBeEmpty();
        (await server.GlobAsync(AppsServer.Neighbour, "**")).ShouldBeEmpty();
        var hisRead = await server.CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = "/glucose/app.json" },
            AppsServer.TurnOf(AppsServer.Him));
        hisRead["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
    }

    // A slug is unique per author: another member may use the same one, and each gets an app of
    // their own, under ids of their own.
    [Fact]
    public async Task ASlug_IsUniquePerAuthor()
    {
        await using var server = await AppsServer.StartAsync();
        var hers = await server.AppAsync(AppsServer.Her, "glucose");

        var again = await server.CreateAsync(AppsServer.Her, "/glucose/app.json", AppsServer.Manifest(), overwrite: false);
        var his = await server.AppAsync(AppsServer.Him, "glucose");

        again["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.AlreadyExists);
        Id(his).ShouldNotBe(Id(hers));
        server.StoredDirectories(AppsServer.Her).Count.ShouldBe(2);
    }

    [Fact]
    public async Task ABrokenManifest_IsRefusedInTheRulesWords_AndNothingIsStored()
    {
        await using var server = await AppsServer.StartAsync();

        var refused = await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose","accentColor":"orange","description":"Mine.","connect":["http://api.example.com"]}""");

        refused["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        refused["message"]!.GetValue<string>().ShouldContain("#rgb, #rrggbb or #rrggbbaa");
        refused["message"]!.GetValue<string>().ShouldContain("'http://api.example.com' is no bare https origin");
        (await server.GlobAsync(AppsServer.Her, "**")).ShouldBeEmpty();
        server.StoredDirectories(AppsServer.Her).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEditBreakingTheManifest_IsRefused_AndTheManifestKept()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");

        var refused = await server.CallAsync("fs_edit", new Dictionary<string, object?>
        {
            ["path"] = "/glucose/app.json",
            ["edits"] = new[] { new { oldString = "\"/sugary\"", newString = "\"sugary\"" } }
        }, AppsServer.TurnOf(AppsServer.Her));

        refused["message"]!.GetValue<string>().ShouldContain("'sugary' is no mount point");
        (await server.ReadAsync(AppsServer.Her, "/glucose/app.json")).ShouldContain("\"/sugary\"");
    }

    // A person's registered agent resolves for its owner alone, so nobody else could read through an
    // app it built: only the household's own agents make one.
    [Fact]
    public async Task ARegisteredAgentsTurn_CannotCreateAnApp_AndIsToldWhy()
    {
        await using var server = await AppsServer.StartAsync();

        var refused = await server.CallAsync("fs_create", new Dictionary<string, object?>
        {
            ["path"] = "/glucose/app.json", ["content"] = AppsServer.Manifest()
        }, AppsServer.TurnOf(AppsServer.Her, agentId: "custom-ana-1"));

        refused["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        refused["message"]!.GetValue<string>().ShouldContain("household's own agents");
        server.StoredDirectories(AppsServer.Her).ShouldBeEmpty();
    }

    [Fact]
    public async Task WritesUnderNodeModules_AreRefused_TextAndBinaryAlike()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");

        var text = await server.CreateAsync(AppsServer.Her, "/glucose/node_modules/react/index.js", "export {}");
        var binary = await server.CallAsync("fs_blob_write", new Dictionary<string, object?>
        {
            ["path"] = "/glucose/web/node_modules/x.wasm", ["contentBase64"] = Convert.ToBase64String([0, 97, 115, 109]),
            ["offset"] = 0, ["overwrite"] = false, ["createDirectories"] = true
        }, AppsServer.TurnOf(AppsServer.Her));

        text["message"]!.GetValue<string>().ShouldContain("node_modules");
        binary["message"]!.GetValue<string>().ShouldContain("node_modules");
        (await server.GlobAsync(AppsServer.Her, "**/node_modules/**")).ShouldBeEmpty();
    }

    // An app's history is kept outside its working tree; nothing named .git is written into one, so no
    // write can reach git's hooks or configuration.
    [Theory]
    [InlineData("/glucose/.git/config")]
    [InlineData("/glucose/web/.git/HEAD")]
    public async Task NothingNamedGit_IsWrittenIntoAnApp(string path)
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");

        var text = await server.CreateAsync(AppsServer.Her, path, "[core]");
        var binary = await server.CallAsync("fs_blob_write", new Dictionary<string, object?>
        {
            ["path"] = path, ["contentBase64"] = Convert.ToBase64String([1, 2, 3]),
            ["offset"] = 0, ["overwrite"] = true, ["createDirectories"] = true
        }, AppsServer.TurnOf(AppsServer.Her));

        text["message"]!.GetValue<string>().ShouldContain("Nothing named .git");
        binary["message"]!.GetValue<string>().ShouldContain("Nothing named .git");
        (await server.GlobAsync(AppsServer.Her, "**")).ShouldNotContain(entry => entry.Contains(".git"));
    }

    // Build output arrives a chunk at a time, as a copy from the sandbox streams it, and reads back
    // byte for byte.
    [Fact]
    public async Task ABinaryFile_IsWrittenInChunks_AndReadBackWhole()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

        foreach (var (offset, length) in new[] { (0, 200_000), (200_000, 100_000) })
        {
            var written = await server.CallAsync("fs_blob_write", new Dictionary<string, object?>
            {
                ["path"] = "/glucose/dist/font.woff2",
                ["contentBase64"] = Convert.ToBase64String(bytes, offset, length),
                ["offset"] = offset, ["overwrite"] = true, ["createDirectories"] = true
            }, AppsServer.TurnOf(AppsServer.Her));
            written["path"]!.GetValue<string>().ShouldBe("/glucose/dist/font.woff2", written.ToJsonString());
        }

        var read = new List<byte>();
        while (read.Count < bytes.Length)
        {
            var chunk = await server.CallAsync("fs_blob_read", new Dictionary<string, object?>
            {
                ["path"] = "/glucose/dist/font.woff2", ["offset"] = read.Count, ["length"] = 256 * 1024
            }, AppsServer.TurnOf(AppsServer.Her));
            read.AddRange(Convert.FromBase64String(chunk["contentBase64"]!.GetValue<string>()));
        }

        read.ShouldBe(bytes);
    }

    [Fact]
    public async Task FilesAreCopiedAndMovedWithinTheApp_AndTheActionsAreListedAsExecutable()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/src/index.html", "<h1>Hi</h1>");

        await server.CallAsync("fs_copy", new Dictionary<string, object?>
        {
            ["sourcePath"] = "/glucose/src", ["destinationPath"] = "/glucose/dist"
        }, AppsServer.TurnOf(AppsServer.Her));
        await server.CallAsync("fs_move", new Dictionary<string, object?>
        {
            ["sourcePath"] = "/glucose/src/index.html", ["destinationPath"] = "/glucose/src/old.html"
        }, AppsServer.TurnOf(AppsServer.Her));
        var glob = await server.CallAsync("fs_glob", new Dictionary<string, object?> { ["pattern"] = "**" },
            AppsServer.TurnOf(AppsServer.Her));

        glob["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ShouldBe(
        [
            "/glucose/", "/glucose/app.json", "/glucose/check", "/glucose/dist/", "/glucose/dist/index.html", "/glucose/history",
            "/glucose/publish", "/glucose/rollback", "/glucose/src/", "/glucose/src/old.html"
        ]);
        glob["executables"]!.AsArray().Select(e => e!.GetValue<string>())
            .ShouldBe(["/glucose/check", "/glucose/history", "/glucose/publish", "/glucose/rollback"], ignoreOrder: true);
        var opened = await server.CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = "/glucose/publish" },
            AppsServer.TurnOf(AppsServer.Her));
        opened["errorCode"]!.GetValue<string>().ShouldNotBeNull();
        opened["hint"]!.GetValue<string>().ShouldContain("./publish");
    }

    // The id, not the slug, names the repository and ends the hostname, so a renamed app keeps it.
    [Fact]
    public async Task MovingAnAppsDirectory_RenamesTheApp_AndKeepsItsId()
    {
        await using var server = await AppsServer.StartAsync();
        var id = Id(await server.AppAsync(AppsServer.Her, "glucose"));

        var moved = await server.CallAsync("fs_move", new Dictionary<string, object?>
        {
            ["sourcePath"] = "/glucose", ["destinationPath"] = "/sugar-today"
        }, AppsServer.TurnOf(AppsServer.Her));

        moved["message"]!.GetValue<string>().ShouldContain($"https://sugar-today-{id}.apps.test/");
        (await server.GlobAsync(AppsServer.Her, "*")).ShouldBe(["/sugar-today/"]);
    }

    // A manifest is checked on every way in: bytes streamed onto it would skip the check.
    [Fact]
    public async Task TheManifest_IsNeverWrittenAsBytes()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");

        var refused = await server.CallAsync("fs_blob_write", new Dictionary<string, object?>
        {
            ["path"] = "/glucose/app.json", ["contentBase64"] = Convert.ToBase64String("{}"u8.ToArray()),
            ["offset"] = 0, ["overwrite"] = true, ["createDirectories"] = true
        }, AppsServer.TurnOf(AppsServer.Her));

        refused["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        (await server.ReadAsync(AppsServer.Her, "/glucose/app.json")).ShouldContain("Glucose today");
    }

    [Fact]
    public async Task RemovingTheDirectory_DeletesTheRepositoryAndItsMetadata()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Hi</h1>");

        var removed = await server.CallAsync("fs_delete", new Dictionary<string, object?> { ["path"] = "/glucose" },
            AppsServer.TurnOf(AppsServer.Her));

        removed["status"]!.GetValue<string>().ShouldBe("deleted", removed.ToJsonString());
        (await server.GlobAsync(AppsServer.Her, "**")).ShouldBeEmpty();
        server.StoredDirectories(AppsServer.Her).ShouldBeEmpty();
    }

    // An app the household shares as an app space is not its author's alone to delete: refused, naming the
    // space an owner removes first, and nothing of it goes. Once unshared it deletes as any other.
    [Fact]
    public async Task AnAppThatIsAnAppSpace_IsNotDeleted_AndTheRefusalNamesTheSpace()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        server.Spaces.Share(AppsServer.Her.TenantId, id, "Our glucose");

        var refused = await server.CallAsync("fs_delete", new Dictionary<string, object?> { ["path"] = "/glucose" },
            AppsServer.TurnOf(AppsServer.Her));

        refused["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied, refused.ToJsonString());
        refused["message"]!.GetValue<string>().ShouldContain("Our glucose");
        (await server.GlobAsync(AppsServer.Her, "*")).ShouldContain("/glucose/");
        server.Spaces.Unshare(AppsServer.Her.TenantId);
        (await server.CallAsync("fs_delete", new Dictionary<string, object?> { ["path"] = "/glucose" },
            AppsServer.TurnOf(AppsServer.Her)))["status"]!.GetValue<string>().ShouldBe("deleted");
    }

    // Whether it is shared cannot be told just now: nothing is deleted, and the agent hears to try again.
    [Fact]
    public async Task AnAppWhoseSpacesCannotBeRead_IsNotDeleted()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        server.Spaces.Down = true;

        var refused = await server.CallAsync("fs_delete", new Dictionary<string, object?> { ["path"] = "/glucose" },
            AppsServer.TurnOf(AppsServer.Her));

        refused["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.TransientDependency, refused.ToJsonString());
        server.StoredDirectories(AppsServer.Her).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task TheAppsPerHousehold_AreLimited_AndTheRefusalNamesTheLimit()
    {
        await using var server = await AppsServer.StartAsync(s => s with { Quotas = s.Quotas with { AppsPerHousehold = 1 } });
        await server.AppAsync(AppsServer.Her, "glucose");

        var refused = await server.CreateAsync(AppsServer.Him, "/lights/app.json", AppsServer.Manifest("Lights"));

        refused["message"]!.GetValue<string>().ShouldContain("apps per household is 1");
    }

    [Fact]
    public async Task TheBytesPerRepository_AreLimited_AndTheRefusalNamesTheLimit()
    {
        await using var server = await AppsServer.StartAsync(s => s with { Quotas = s.Quotas with { BytesPerRepository = 64 * 1024 } });
        await server.AppAsync(AppsServer.Her, "glucose");

        var refused = await server.CreateAsync(AppsServer.Her, "/glucose/big.js", new string('x', 100 * 1024));

        refused["message"]!.GetValue<string>().ShouldContain("bytes per repository is 65536");
        (await server.GlobAsync(AppsServer.Her, "glucose/big.js")).ShouldBeEmpty();
    }

    private static string Id(string note) => Regex.Match(note, @"\(id ([a-z0-9]{6})\)").Groups[1].Value;
}