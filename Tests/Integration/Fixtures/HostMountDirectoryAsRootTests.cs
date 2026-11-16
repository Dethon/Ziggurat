using Shouldly;

namespace Tests.Integration.Fixtures;

// A container that writes its mount as root leaves files this process cannot remove, and a slot that
// cannot be emptied is never handed out again. One more run of the same image, as root, empties it.
public class HostMountDirectoryAsRootTests
{
    private const string Image = "redis/redis-stack-server:latest";

    // Empty is what lets the slot be handed out again; which slot a later claim gets is a race with
    // whatever other test process is claiming the same name, so it is not asserted.
    [Fact]
    public async Task AMountARootContainerWroteIn_IsEmptiedThroughItsImage()
    {
        // A fixed name: a fresh one per run would be the leak this guards against.
        using var mount = HostMountDirectory.Claim("as-root-test");
        await RunAsRootAsync(mount.Path, "mkdir -p /m/.storage/deep && echo x > /m/.storage/deep/f && echo y > /m/top");

        (await mount.TryEmptyAsRootAsync(Image)).ShouldBeTrue();

        Directory.EnumerateFileSystemEntries(mount.Path).ShouldBeEmpty();
    }

    private static async Task RunAsRootAsync(string path, string script)
    {
        await using var writer = TestContainers.Container(Image, "mount-writer")
            .WithEntrypoint("sh", "-c", script)
            .WithBindMount(path, "/m")
            .WithWaitStrategy(TestContainers.OneShot())
            .Build();
        await writer.StartAsync();
    }
}