using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.E2E.Sandbox;

// The two guarantees on the shell route, in the real image: a write the person would have been
// asked about is refused rather than run, and a command killed by its timeout commits nothing.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxKillAndApprovalE2ETests(SandboxE2EFixture fixture)
{
    private readonly MemoryDisk _vault = new("vault", new Dictionary<string, string> { ["a.md"] = "old\n" });

    private VfsCall Mint(Func<string, bool> permits) =>
        fixture.Bridge.Mint(BridgeFixtures.Registry((_vault, "/vault", null)), permits, null);

    // Killed mid-write: an open file the kill closes and a new file still held both reach the
    // bridge after the revocation, and are dropped.
    [SkippableFact]
    public async Task ACommandKilledByItsTimeoutMidWrite_LeavesTheMountUnchanged()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint(_ => true);

        var result = await ExecAsync(client,
            "echo partial > /vault/new.md; exec 3>/vault/a.md; echo half >&3; sleep 30", call, cts.Token, timeoutSeconds: 2);

        result.GetProperty("timedOut").GetBoolean().ShouldBeTrue(result.ToString());
        _vault.Files.ShouldBe(new Dictionary<string, string> { ["a.md"] = "old\n" });
        call.Changes.Select(c => (c.Path, c.Status)).ShouldBe(
            [("/vault/a.md", VfsChange.Statuses.Dropped), ("/vault/new.md", VfsChange.Statuses.Dropped)],
            ignoreOrder: true);
    }

    // The text tool would ask the person, so the shell is refused — EACCES at the close bash
    // swallows — the refusal listed, and nobody is asked.
    [SkippableFact]
    public async Task AWriteTheToolWouldAskAbout_IsRefusedAndListed()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint(tool => tool != VfsTextCreateTool.Name);

        await ExecAsync(client, "echo x > /vault/a.md", call, cts.Token);

        _vault.Files["a.md"].ShouldBe("old\n");
        var change = call.Changes.ShouldHaveSingleItem();
        (change.Path, change.Status).ShouldBe(("/vault/a.md", VfsChange.Statuses.Refused));
        change.Error!["errorCode"]!.GetValue<string>().ShouldBe("permission_denied");
    }

    private static async Task<JsonElement> ExecAsync(
        McpClient client, string command, VfsCall call, CancellationToken ct, int? timeoutSeconds = null)
    {
        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "fs_exec",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(""),
                ["command"] = JsonSerializer.SerializeToElement(command),
                ["timeoutSeconds"] = JsonSerializer.SerializeToElement(timeoutSeconds)
            },
            Meta = new JsonObject { [VfsBridgeGrant.MetaKey] = new VfsBridgeGrant(call.Token).ToMeta() }
        }, cancellationToken: ct);

        return JsonDocument.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))
            .RootElement.Clone();
    }
}