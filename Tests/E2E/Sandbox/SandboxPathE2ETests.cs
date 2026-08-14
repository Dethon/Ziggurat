using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sandbox;

// One rule, proved where the image is real: the mount point names the container root inside the
// container too, so a path spelled the way the filesystem prompt teaches resolves whether it
// arrives as an argument or inside a command string.
//
// This cannot be tested any lower. The registry, the exec tool and the command runner all agree
// about the mount point in process; the container is the one participant that has never heard of
// it, and only the Dockerfile can tell it.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxPathE2ETests(SandboxE2EFixture fixture)
{
    [SkippableFact]
    public async Task ACommandNamingAFileByItsMountPrefixedPath_ResolvesIt()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var prefixed = await ExecAsync(client, "cat /sandbox/etc/os-release", cts.Token);

        prefixed.GetProperty("exitCode").GetInt32().ShouldBe(0, prefixed.ToString());
        prefixed.GetProperty("stdout").GetString()!.ShouldContain("PRETTY_NAME");
    }

    // The spelling that already worked keeps working, which is the whole point of an alias rather
    // than a rewrite: paths taken from earlier command output need no translation before reuse.
    [SkippableFact]
    public async Task TheSameFileNamedByItsContainerNativePath_ResolvesToTheSameBytes()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var prefixed = await ExecAsync(client, "cat /sandbox/etc/os-release", cts.Token);
        var native = await ExecAsync(client, "cat /etc/os-release", cts.Token);

        native.GetProperty("exitCode").GetInt32().ShouldBe(0, native.ToString());
        native.GetProperty("stdout").GetString().ShouldBe(prefixed.GetProperty("stdout").GetString());
    }

    // Containment terminates on the alias rather than chasing it: a path jail resolves link
    // targets, and /sandbox resolves to the root it is already judged against. The other half —
    // that a recursive walk never traverses it — is the property
    // LocalFileSystemClientTests.Glob_SymlinksInsideTheTree_AreNotFollowedOrListed pins, and
    // globbing the container root here would walk the whole image to say it again.
    [SkippableFact]
    public async Task AskingAboutTheAliasItself_Answers()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_info", new Dictionary<string, object?>
        {
            ["path"] = "sandbox"
        }, cancellationToken: cts.Token);

        var info = Parse(result);
        info.GetProperty("exists").GetBoolean().ShouldBeTrue(info.ToString());
        info.GetProperty("isDirectory").GetBoolean().ShouldBeTrue(info.ToString());
    }

    private static async Task<JsonElement> ExecAsync(McpClient client, string command, CancellationToken ct)
    {
        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = "",
            ["command"] = command
        }, cancellationToken: ct);

        return Parse(result);
    }

    private static JsonElement Parse(CallToolResult result) =>
        JsonDocument.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))
            .RootElement.Clone();
}