using Domain.Agents;
using Domain.DTOs.Channel;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sandbox;

// A file someone sent becomes a real file the agent can run something against — proved where the
// image, the volume and the unprivileged user are real. Every lower layer builds the sandbox
// against a temporary root the test user owns outright, so none of them can tell a writable
// directory from an unwritable one, and that blindness is why a landing that never landed anything
// shipped green.
//
// This drives the shipped chain end to end: discovery reads the workspace the mount publishes, and
// landing composes its target from it.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxLandingE2ETests(SandboxE2EFixture fixture)
{
    private static readonly AttachmentReference _notes = new()
    {
        Id = "7-42/abc",
        FileName = "notes.txt",
        MediaType = "text/plain",
        SizeBytes = 8
    };

    [SkippableFact]
    public async Task AnAttachment_LandsInTheWorkspaceAndIsReadBackThroughTheFilesystemTools()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var registry = await MountAsync(client, cts.Token);

        var landed = await AttachmentLanding.LandAsync(
            new AttachmentLanding.Landing(
                registry, [_notes], (_, _) => Task.FromResult<byte[]?>("sent bytes"u8.ToArray()),
                "7:42", "turn-abc"),
            NullLogger.Instance,
            cts.Token);

        landed.Failed.ShouldBeEmpty();
        string path = landed.Landed.ShouldHaveSingleItem();
        path.ShouldBe("/sandbox/home/sandbox_user/uploads/7-42/turn-abc/notes.txt");

        // The path the agent is given, read the way the agent would read it.
        var read = await new VfsTextReadTool(registry).RunAsync(path, cancellationToken: cts.Token);
        read!.ToJsonString().ShouldContain("sent bytes");
    }

    // A conversation resumed tomorrow can still act on what was sent today, which is only true
    // because the workspace is a volume. Bought with a real restart: the container's own layer is
    // thrown away with it, so a file that reads back afterwards is one the volume kept.
    [SkippableFact]
    public async Task ALandedFile_IsStillThereAfterTheContainerIsRestarted()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        string path;
        await using (var client = await fixture.ConnectAsync(cts.Token))
        {
            var landed = await AttachmentLanding.LandAsync(
                new AttachmentLanding.Landing(
                    await MountAsync(client, cts.Token), [_notes],
                    (_, _) => Task.FromResult<byte[]?>("kept bytes"u8.ToArray()),
                    "7:42", "turn-volume"),
                NullLogger.Instance,
                cts.Token);
            path = landed.Landed.ShouldHaveSingleItem();
        }

        await fixture.RestartAsync(cts.Token);

        await using var reconnected = await fixture.ConnectAsync(cts.Token);
        var registry = await MountAsync(reconnected, cts.Token);
        var read = await new VfsTextReadTool(registry).RunAsync(path, cancellationToken: cts.Token);

        read!.ToJsonString().ShouldContain("kept bytes");
    }

    // The defect itself, pinned where it is real: the container root is root-owned and the server
    // runs unprivileged, so the target the landing code used to build could never have been written.
    [SkippableFact]
    public async Task TheContainerRoot_CannotBeWrittenByTheUnprivilegedUser()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var registry = await MountAsync(client, cts.Token);

        var created = await new VfsTextCreateTool(registry).RunAsync(
            "/sandbox/uploads/7-42/turn-abc/notes.txt", "sent bytes",
            createDirectories: true, cancellationToken: cts.Token);

        // The reason is named rather than "some error", so this cannot pass for something that has
        // nothing to do with who the container runs as.
        created!["ok"]!.GetValue<bool>().ShouldBeFalse(created.ToJsonString());
        created["message"]!.GetValue<string>().ShouldContain("/uploads");
        created["message"]!.GetValue<string>().ShouldContain("denied");
    }

    private static async Task<VirtualFileSystemRegistry> MountAsync(
        McpClient client, CancellationToken ct)
    {
        var registry = new VirtualFileSystemRegistry();
        await McpFileSystemDiscovery.DiscoverAndMountAsync([client], registry, NullLogger.Instance, ct);
        return registry;
    }
}