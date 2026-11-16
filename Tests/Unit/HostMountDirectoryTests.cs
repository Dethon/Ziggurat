using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Unit;

public class HostMountDirectoryTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "host-mount-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TwoClaimsOfOneName_AreTwoDirectories()
    {
        using var first = HostMountDirectory.Claim("thing", _root);
        using var second = HostMountDirectory.Claim("thing", _root);

        first.Path.ShouldNotBe(second.Path);
        Directory.Exists(first.Path).ShouldBeTrue();
        Directory.Exists(second.Path).ShouldBeTrue();
    }

    [Fact]
    public void AReleasedDirectory_IsHandedOutAgain_EmptyButTheSameDirectory()
    {
        string path;
        long inode;
        using (var first = HostMountDirectory.Claim("thing", _root))
        {
            path = first.Path;
            inode = Inode(path);
            Directory.CreateDirectory(Path.Combine(path, "nested"));
            File.WriteAllText(Path.Combine(path, "nested", "file"), "left behind");
            File.WriteAllText(Path.Combine(path, "top"), "left behind");
        }

        using var again = HostMountDirectory.Claim("thing", _root);

        again.Path.ShouldBe(path);
        Inode(again.Path).ShouldBe(inode, "a directory deleted and made again is another inode, which Docker's staging mount no longer shows");
        Directory.EnumerateFileSystemEntries(again.Path).ShouldBeEmpty();
    }

    private static long Inode(string path) =>
        long.Parse(System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat", ["-c", "%i", path])
        {
            RedirectStandardOutput = true
        })!.StandardOutput.ReadToEnd().Trim());
}