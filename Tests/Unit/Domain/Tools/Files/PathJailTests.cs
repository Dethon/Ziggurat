using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class PathJailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jail-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"jail-outside-{Guid.NewGuid():N}");
    private readonly PathJail _jail;

    public PathJailTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _jail = new PathJail(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        if (Directory.Exists(_outside))
        {
            Directory.Delete(_outside, true);
        }
    }

    // Creation fails without symlink permission (e.g. Windows without developer mode); the symlink
    // tests pass vacuously there rather than fail on the platform.
    private static bool TryCreateSymlink(string link, string target, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // Lexically the path sits under the root, but the directory it goes through is a symlink out
    // of the jail — reading through it would serve foreign bytes.
    [Fact]
    public void Contains_AFileBehindADirectorySymlinkPointingOutside_IsOutside()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "foreign");
        if (!TryCreateSymlink(Path.Combine(_root, "escape"), _outside, directory: true))
        {
            return;
        }

        _jail.Contains(Path.Combine(_root, "escape", "secret.txt")).ShouldBeFalse();
        _jail.TryResolve("escape/secret.txt", out _).ShouldBeFalse();
    }

    [Fact]
    public void Contains_AFileSymlinkPointingOutside_IsOutside()
    {
        var target = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(target, "foreign");
        if (!TryCreateSymlink(Path.Combine(_root, "alias.txt"), target, directory: false))
        {
            return;
        }

        _jail.Contains(Path.Combine(_root, "alias.txt")).ShouldBeFalse();
    }

    [Fact]
    public void Contains_ASymlinkPointingInsideTheRoot_IsInside()
    {
        var target = Path.Combine(_root, "real");
        Directory.CreateDirectory(target);
        if (!TryCreateSymlink(Path.Combine(_root, "alias"), target, directory: true))
        {
            return;
        }

        _jail.Contains(Path.Combine(_root, "alias", "note.md")).ShouldBeTrue();
        _jail.TryResolve("alias/note.md", out _).ShouldBeTrue();
    }

    // A leaf that does not exist yet is judged by where its parent physically lives — writing
    // "escape/new.bin" would create the file outside.
    [Fact]
    public void TryResolve_AMissingLeafUnderAnOutwardSymlinkedDirectory_IsRefused()
    {
        if (!TryCreateSymlink(Path.Combine(_root, "escape"), _outside, directory: true))
        {
            return;
        }

        _jail.TryResolve("escape/new.bin", out _).ShouldBeFalse();
    }

    // A broken symlink still has a target: opening it for write would create the file there.
    [Fact]
    public void Contains_ABrokenSymlinkPointingOutside_IsOutside()
    {
        if (!TryCreateSymlink(Path.Combine(_root, "dangling.txt"),
                Path.Combine(_outside, "not-yet.txt"), directory: false))
        {
            return;
        }

        _jail.Contains(Path.Combine(_root, "dangling.txt")).ShouldBeFalse();
    }

    // A pair of symlinks pointing at each other has no final target, and asking for one throws.
    // Containment is a question every tool asks before it does anything, so the answer has to be
    // "no" — the denied envelope the tool sites already handle — never an exception out of the jail.
    [Fact]
    public void Contains_ASymlinkCycle_IsOutsideAndDoesNotThrow()
    {
        var first = Path.Combine(_root, "loop-a");
        var second = Path.Combine(_root, "loop-b");
        if (!TryCreateSymlink(first, second, directory: false) ||
            !TryCreateSymlink(second, first, directory: false))
        {
            return;
        }

        _jail.Contains(first).ShouldBeFalse();
        _jail.TryResolve("loop-a", out _).ShouldBeFalse();
    }

    [Fact]
    public void Root_IsCanonicalAndHasNoTrailingSeparator()
    {
        var jail = new PathJail(_root + Path.DirectorySeparatorChar);

        jail.Root.ShouldBe(Path.GetFullPath(_root));
    }

    // The sandbox mounts the container root itself, where the root already ends in a separator.
    [Fact]
    public void Contains_WhenTheRootIsTheFilesystemRoot_PathsUnderItAreInside()
    {
        var jail = new PathJail(Path.GetPathRoot(Path.GetTempPath())!);

        jail.Contains(Path.GetTempPath()).ShouldBeTrue();
        jail.TryResolve(Path.Combine(Path.GetTempPath(), "note.md"), out _).ShouldBeTrue();
    }

    [Fact]
    public void Contains_TheRootItself_IsInside()
    {
        _jail.Contains(_jail.Root).ShouldBeTrue();
    }

    // The hole this type closes: a prefix match without a separator let /library-backup pass as
    // though it were inside /library.
    [Fact]
    public void Contains_ASiblingWhoseNameExtendsTheRoot_IsOutside()
    {
        _jail.Contains(_jail.Root + "-backup").ShouldBeFalse();
        _jail.Contains(_jail.Root + "-backup" + Path.DirectorySeparatorChar + "note.md").ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_RelativePath_IsCombinedWithTheRoot()
    {
        Resolved("docs/note.md").ShouldBe(Path.Combine(_jail.Root, "docs", "note.md"));
    }

    [Fact]
    public void TryResolve_AbsolutePathInsideTheRoot_IsAccepted()
    {
        var inside = Path.Combine(_jail.Root, "note.md");

        Resolved(inside).ShouldBe(inside);
    }

    // A path reached through a relative segment is judged by where it lands, not how it is spelt.
    [Fact]
    public void TryResolve_RelativeSegmentThatStaysInside_IsAccepted()
    {
        Resolved("docs/../note.md").ShouldBe(Path.Combine(_jail.Root, "note.md"));
    }

    [Fact]
    public void TryResolve_RelativeSegmentThatEscapes_IsRefused()
    {
        _jail.TryResolve("../elsewhere/note.md", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_AbsolutePathOutsideTheRoot_ReturnsFalseWithoutThrowing()
    {
        _jail.TryResolve("/etc/passwd", out var full).ShouldBeFalse();
        full.ShouldBeNull();
    }

    private string Resolved(string path)
    {
        _jail.TryResolve(path, out var full).ShouldBeTrue();
        return full!;
    }
}