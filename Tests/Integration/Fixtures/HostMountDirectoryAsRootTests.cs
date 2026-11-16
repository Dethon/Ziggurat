using Shouldly;

namespace Tests.Integration.Fixtures;

// A container that writes its mount as root leaves files this process cannot remove, and a slot that
// cannot be emptied is never handed out again. One more run of the same image, as root, empties it.
public class HostMountDirectoryAsRootTests
{
    private const string Image = "redis/redis-stack-server:latest";

    [Fact]
    public async Task AMountARootContainerWroteIn_IsEmptiedThroughItsImage_AndHandedOutAgain()
    {
        // A fixed name: a fresh one per run would be the leak this guards against.
        const string name = "as-root-test";
        string path;
        using (var mount = HostMountDirectory.Claim(name))
        {
            path = mount.Path;
            await RunAsRootAsync(mount.Path, "mkdir -p /m/.storage/deep && echo x > /m/.storage/deep/f && echo y > /m/top");

            (await mount.TryEmptyAsRootAsync(Image)).ShouldBeTrue();
        }

        using var again = HostMountDirectory.Claim(name);
        again.Path.ShouldBe(path);
        Directory.EnumerateFileSystemEntries(again.Path).ShouldBeEmpty();
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