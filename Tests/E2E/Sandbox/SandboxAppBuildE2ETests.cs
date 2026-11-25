using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using McpServerApps.Store;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Integration.Fixtures;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.E2E.Sandbox;

// An npm app's build output reaching /apps the way an agent with a sandbox sends it: a command copies
// what the build left in the home into the app, over the real image's FUSE daemon and the bridge, onto
// the apps server mounted as the agent holds it — discovered over MCP, its operations what the server
// published, every call signed for the person whose turn ran the command. Nothing here is new
// mechanism; what this holds is that the mount's declared operations, its refusals and its publish
// carry a build's bytes, text and binary alike, unchanged from the home to what the app serves.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxAppBuildE2ETests(SandboxE2EFixture fixture) : IAsyncLifetime
{
    private const string Slug = "glucose";

    // An npm project's manifest names its build output as what the app serves.
    private const string Manifest =
        """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","reads":["/sugary"],"root":"dist"}""";

    // What a build leaves in dist/: pages, script and style the app's own (only local files, so publish
    // has no outside code to refuse), and the binary assets a bundler copies beside them. The script is
    // text with CRLF endings, characters outside ASCII and no final newline — text crosses the bridge
    // as a string, which is where a byte could change. The image is a few hundred kilobytes, as a
    // real one is: the bridge hands the mount each file whole, in one blob write.
    private static readonly IReadOnlyDictionary<string, byte[]> _dist = new Dictionary<string, byte[]>
    {
        ["index.html"] = Encoding.UTF8.GetBytes(
            "<!doctype html>\n<html lang=\"es\">\n<head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"assets/style.css\"></head>\n"
            + "<body><h1>Glucosa de hoy</h1><img src=\"assets/logo.png\" alt=\"\"><script type=\"module\" src=\"assets/app.js\"></script></body>\n</html>\n"),
        ["assets/app.js"] = Encoding.UTF8.GetBytes(
            "const title = 'Glucosa — día';\r\nconst wasm = await WebAssembly.instantiateStreaming(fetch('./curve.wasm'));\r\n"
            + "document.title = title + ' ✓';"),
        ["assets/style.css"] = Encoding.UTF8.GetBytes(
            "@font-face { font-family: Body; src: url(body.woff2) format('woff2'); }\nbody { font-family: Body, sans-serif; }\n"),
        ["assets/body.woff2"] = Binary("wOF2"u8, 20_000, seed: 1),
        ["assets/logo.png"] = Binary([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 300_000, seed: 2),
        ["assets/curve.wasm"] = Binary([0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00], 9_000, seed: 3)
    };

    // The dependency tree an npm install leaves beside the build: a package's text and a native binary,
    // which reach the mount by the two different writes the bridge has.
    private static readonly IReadOnlyDictionary<string, byte[]> _nodeModules = new Dictionary<string, byte[]>
    {
        ["node_modules/left-pad/index.js"] = "module.exports = s => s;\n"u8.ToArray(),
        ["node_modules/@esbuild/linux-x64/bin/esbuild"] = Binary("\u007fELF"u8, 4_000, seed: 4)
    };

    // The project's own files, which the repository keeps beside its build: its .gitignore names the
    // build output, as an npm project's does.
    private static readonly IReadOnlyDictionary<string, byte[]> _project = new Dictionary<string, byte[]>
    {
        [".gitignore"] = "node_modules/\ndist/\n"u8.ToArray(),
        ["package.json"] = """{"name":"glucose","private":true,"scripts":{"build":"vite build"}}"""u8.ToArray()
    };

    // What the npm build case's build script writes: a page, and an image whose bytes hold a NUL and a
    // byte past ASCII, so the binary write is what carries it.
    private static readonly IReadOnlyDictionary<string, byte[]> _built = new Dictionary<string, byte[]>
    {
        ["index.html"] = "<!doctype html>\n<h1>Glucosa de hoy</h1>\n"u8.ToArray(),
        ["assets/logo.png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0x7F, 0x00]
    };

    private readonly string _build = $"build-{Guid.NewGuid():N}";

    // Where the command finds the build: the home, which is the mount's workspace and not its root.
    private string InTheContainer => $"{SandboxE2EFixture.ContainerWorkspace}/{_build}";
    private AppsServer? _apps;
    private McpClient? _appsClient;
    private string _id = "";

    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        _apps = await AppsServer.StartAsync();
        _appsClient = await McpClient.CreateAsync(McpTestSecret.Transport(_apps.Endpoint));
        var created = await _apps.CreateAsync(AppsServer.Her, $"/{Slug}/app.json", Manifest);
        _id = AppsServer.IdIn(created["note"]?.GetValue<string>() ?? throw new InvalidOperationException(created.ToJsonString()));
    }

    public async Task DisposeAsync()
    {
        if (_appsClient is not null)
        {
            await _appsClient.DisposeAsync();
        }

        if (_apps is not null)
        {
            await _apps.DisposeAsync();
        }

        // The build, and what npm keeps in the home beside it.
        foreach (var left in new[] { _build, ".npm", ".npm-global" }.Select(name => Path.Combine(fixture.WorkspaceOnHost, name))
                     .Where(Directory.Exists))
        {
            Directory.Delete(left, recursive: true);
        }
    }

    [SkippableFact]
    public async Task CopyingTheBuildOutputIntoAnApp_LandsEveryFileByteForByte()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        LeaveInTheHome(_dist.ToDictionary(f => $"dist/{f.Key}", f => f.Value));
        var call = await MintAsync(cts.Token);

        var result = await ExecAsync($"cp -r {InTheContainer}/dist /apps/{Slug}/ && echo copied", call, cts.Token);

        Stdout(result).ShouldBe("copied\n", result.ToString());
        Changes(call).ShouldBe(
            _dist.Keys.Select(path => ($"/apps/{Slug}/dist/{path}", "create", "applied")),
            ignoreOrder: true, customMessage: Described(call));
        _dist.Where(f => !File.ReadAllBytes(Path.Combine(WorkTree(), "dist", f.Key)).SequenceEqual(f.Value)).Select(f => f.Key)
            .ShouldBeEmpty("every file in the app's working tree is the one the build left");
    }

    [SkippableFact]
    public async Task CopyingTheProjectWithItsNodeModules_RefusesEveryDependencyAndStoresNoneOfIt()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        LeaveInTheHome(_dist.ToDictionary(f => $"dist/{f.Key}", f => f.Value).Concat(_nodeModules).Concat(_project)
            .ToDictionary());
        var call = await MintAsync(cts.Token);

        await ExecAsync($"cp -r {InTheContainer}/. /apps/{Slug}/", call, cts.Token);

        var refused = call.Changes.Where(c => c.Path.Contains("/node_modules/", StringComparison.Ordinal)).ToList();
        refused.Select(c => c.Path).ShouldBe(_nodeModules.Keys.Select(path => $"/apps/{Slug}/{path}"), ignoreOrder: true,
            customMessage: Described(call));
        refused.ShouldAllBe(c => c.Status == "refused" && c.Error!["errorCode"]!.GetValue<string>() == "permission_denied",
            Described(call));
        call.Changes.Except(refused).ShouldAllBe(c => c.Status == "applied", Described(call));
        Directory.Exists(Path.Combine(WorkTree(), "node_modules")).ShouldBeFalse("nothing of the dependency tree is kept");
        (await _apps!.GlobAsync(AppsServer.Her, "**/*", $"/{Slug}")).ShouldNotContain(e => e.Contains("node_modules"));
    }

    [SkippableFact]
    public async Task PublishingAfterTheCopy_ServesTheCopiedTreeFromDist()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        LeaveInTheHome(_dist.ToDictionary(f => $"dist/{f.Key}", f => f.Value).Concat(_project).ToDictionary());
        var call = await MintAsync(cts.Token);
        await ExecAsync($"cp -r {InTheContainer}/. /apps/{Slug}/", call, cts.Token);
        call.Changes.ShouldAllBe(c => c.Status == "applied", Described(call));

        // A bare action skips the sandbox: the agent hands ./publish to the mount itself.
        var published = await _apps!.ExecAsync(AppsServer.Her, $"/{Slug}", "./publish");

        published["exitCode"]!.GetValue<int>().ShouldBe(0, published.ToJsonString());
        var app = (await _apps.Store.FindAsync(_id, cts.Token)).ShouldNotBeNull();
        var served = app.PublishedCommit.ShouldNotBeNull();
        foreach (var (path, bytes) in _dist)
        {
            (await _apps.Store.Repository(app).EntryAsync(served, $"dist/{path}", cts.Token))
                .ShouldBeOfType<CommittedEntry.File>($"dist/{path} is in the served commit").Bytes.ShouldBe(bytes);
        }

        var host = _apps.Published(Slug, _id);
        var session = _apps.SessionFor(AppsServer.Her, host);
        foreach (var (path, bytes) in _dist.Where(f => f.Key != "index.html"))
        {
            var response = await _apps.GetAsync(host, $"/{path}", session);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            (await response.Content.ReadAsByteArrayAsync(cts.Token)).ShouldBe(bytes, path);
        }

        // The page is served with the origin's own tags put in, so it is held to what it says.
        var page = await _apps.GetAsync(host, "/", session, navigate: true);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync(cts.Token)).ShouldContain("<h1>Glucosa de hoy</h1>");
    }

    // An npm project built in the home by the command's user, with what the image ships and nothing from
    // the registry: install writes the lockfile, ci installs from it, the build script (plain node, as a
    // bundler is) writes dist/, and the same command copies dist/ into the app. npm keeps its cache in
    // the home, so nothing here needs a path the user cannot write.
    [SkippableFact]
    public async Task BuildingAnNpmProjectInTheHomeAndCopyingItsDist_LandsWhatTheBuildWrote()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        LeaveInTheHome(new Dictionary<string, byte[]>
        {
            ["package.json"] = """{"name":"glucose","version":"1.0.0","private":true,"scripts":{"build":"node build.js"}}"""u8.ToArray(),
            ["build.js"] = Encoding.UTF8.GetBytes(
                "const fs = require('node:fs');\n"
                + "fs.mkdirSync('dist/assets', { recursive: true });\n"
                + $"fs.writeFileSync('dist/index.html', {JsonSerializer.Serialize(Encoding.UTF8.GetString(_built["index.html"]))});\n"
                + $"fs.writeFileSync('dist/assets/logo.png', Buffer.from('{Convert.ToHexStringLower(_built["assets/logo.png"])}', 'hex'));\n")
        });
        var call = await MintAsync(cts.Token);

        var result = await ExecAsync(
            $"cd {InTheContainer} && npm install --no-audit --no-fund --loglevel=error && npm ci --no-audit --no-fund --loglevel=error "
            + $"&& npm run build --silent && test -f package-lock.json && cp -r dist /apps/{Slug}/ && echo built",
            call, cts.Token);

        Stdout(result).ShouldEndWith("built\n", Case.Sensitive, result.ToString());
        Changes(call).ShouldBe(
            _built.Keys.Select(path => ($"/apps/{Slug}/dist/{path}", "create", "applied")),
            ignoreOrder: true, customMessage: Described(call));
        _built.Where(f => !File.ReadAllBytes(Path.Combine(WorkTree(), "dist", f.Key)).SequenceEqual(f.Value)).Select(f => f.Key)
            .ShouldBeEmpty("every file in the app's working tree is the one the build wrote");
    }

    // npm's defaults put its global prefix in /usr/local, which a command's user cannot write: the image
    // moves it to the home, where its cache already is, and puts its bin on a login shell's PATH, so a
    // tool installed globally runs by name in a later command.
    [SkippableFact]
    public async Task InstallingAToolGlobally_LandsInTheHomeAndRunsByNameInTheNextCommand()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        LeaveInTheHome(new Dictionary<string, byte[]>
        {
            ["tool/package.json"] = """{"name":"greet-tool","version":"1.0.0","bin":{"greet":"greet.js"}}"""u8.ToArray(),
            ["tool/greet.js"] = "#!/usr/bin/env node\nconsole.log('hello from greet');\n"u8.ToArray()
        });
        var call = await MintAsync(cts.Token);

        var installed = await ExecAsync(
            $"npm config get prefix; npm config get cache; npm install -g --no-audit --no-fund --loglevel=error {InTheContainer}/tool >/dev/null && echo installed",
            call, cts.Token);
        var ran = await ExecAsync("greet", call, cts.Token);

        Stdout(installed).ShouldBe(
            $"{SandboxE2EFixture.ContainerWorkspace}/.npm-global\n{SandboxE2EFixture.ContainerWorkspace}/.npm\ninstalled\n",
            installed.ToString());
        Stdout(ran).ShouldBe("hello from greet\n", ran.ToString());
    }

    // The Node the image pins (McpServerSandbox/Dockerfile's NODE_VERSION), with its npm and corepack, as
    // the command's user sees them: a version bump is a decision, so drift reddens here. The install is
    // root's, so the command user cannot change what every later command runs.
    [SkippableFact]
    public async Task TheImage_CarriesThePinnedNodeWithNpmAndCorepack_WhichTheCommandUserCannotChange()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var call = await MintAsync(cts.Token);

        var result = await ExecAsync(
            "node --version; npm --version; corepack --version; "
            + "for path in $(dirname \"$(command -v node)\") /usr/local/lib/node_modules /usr/local/lib/node_modules/npm; do "
            + "test -w \"$path\" && echo \"writable: $path\"; done; echo checked",
            call, cts.Token);

        Stdout(result).ShouldBe("v24.22.3\n11.19.1\n0.36.0\nchecked\n", result.ToString());
    }

    // The build as it stands in the home when the command that copies it runs.
    private void LeaveInTheHome(IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (path, bytes) in files)
        {
            var full = Path.Combine(fixture.WorkspaceOnHost, _build, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
    }

    // The session's mounts as discovery builds them from the apps server, and a call acting for the
    // person whose turn ran the command: the apps server refuses a call for nobody.
    private async Task<VfsCall> MintAsync(CancellationToken ct)
    {
        var registry = new VirtualFileSystemRegistry();
        (await McpFileSystemDiscovery.DiscoverAndMountAsync(
            [_appsClient!], new HashSet<McpClient>(), registry, NullLogger.Instance, ct)).ShouldBeEmpty();
        var call = fixture.Bridge.Mint(registry, BridgeFixtures.Everything, null,
            AppsServer.TurnOf(AppsServer.Her));
        call.ServedNames.ShouldBe(["apps"]);
        return call;
    }

    private string WorkTree() => Path.Combine(_apps!.Settings.StoragePath, AppsServer.Her.TenantId, _id, "tree");

    private static byte[] Binary(ReadOnlySpan<byte> magic, int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        magic.CopyTo(bytes);
        // A NUL past the magic, so the file is binary however the random bytes fell.
        bytes[magic.Length] = 0;
        return bytes;
    }

    private static IReadOnlyList<(string, string, string)> Changes(VfsCall call) =>
        [.. call.Changes.Select(c => (c.Path, c.Operation, c.Status))];

    private static string Described(VfsCall call) =>
        string.Join("\n", call.Changes.Select(c => $"{c.Operation} {c.Path} {c.Status} {c.Error?.ToJsonString()}"));

    private static string Stdout(JsonElement result) =>
        result.TryGetProperty("stdout", out var stdout) ? stdout.GetString()! : result.ToString();

    private async Task<JsonElement> ExecAsync(string command, VfsCall call, CancellationToken ct)
    {
        await using var client = await fixture.ConnectAsync(ct);
        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "fs_exec",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(""),
                ["command"] = JsonSerializer.SerializeToElement(command)
            },
            Meta = new JsonObject { [VfsBridgeGrant.MetaKey] = new VfsBridgeGrant(call.Token).ToMeta() }
        }, cancellationToken: ct);

        return JsonDocument.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))
            .RootElement.Clone();
    }
}