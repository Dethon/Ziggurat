using System.Text.Json.Nodes;
using Domain.Tools;
using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class CopyToolTests : IDisposable
{
    private readonly string _root;
    private readonly TestableCopyTool _tool;

    public CopyToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"copytool-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
        _tool = new TestableCopyTool(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Run_FileToNewFile_CopiesContent()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "hello");

        var result = _tool.TestRun("src.txt", "dst.txt", overwrite: false, createDirectories: true);

        File.ReadAllText(Path.Combine(_root, "dst.txt")).ShouldBe("hello");
        result["status"]!.GetValue<string>().ShouldBe("copied");
        result["bytes"]!.GetValue<long>().ShouldBe(5);
    }

    [Fact]
    public void Run_DestinationExistsAndOverwriteFalse_ReturnsAlreadyExists()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "dst.txt"), "y");

        _tool.TestRun("src.txt", "dst.txt", overwrite: false, createDirectories: true)
            .ShouldBeError(ToolError.Codes.AlreadyExists);
    }

    [Fact]
    public void Run_DestinationExistsAndOverwriteTrue_Replaces()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "new");
        File.WriteAllText(Path.Combine(_root, "dst.txt"), "old");

        _tool.TestRun("src.txt", "dst.txt", overwrite: true, createDirectories: true);

        File.ReadAllText(Path.Combine(_root, "dst.txt")).ShouldBe("new");
    }

    [Fact]
    public void Run_DirectorySource_CopiesRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "A");
        File.WriteAllText(Path.Combine(_root, "src", "sub", "b.txt"), "B");

        _tool.TestRun("src", "dst", overwrite: false, createDirectories: true);

        File.ReadAllText(Path.Combine(_root, "dst", "a.txt")).ShouldBe("A");
        File.ReadAllText(Path.Combine(_root, "dst", "sub", "b.txt")).ShouldBe("B");
    }

    [Fact]
    public void Run_PathToSiblingDirectoryWithRootPrefix_ReturnsInvalidArgument()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "secret.txt"), "leak");
            var rootName = Path.GetFileName(_root);
            var malicious = $"../{rootName}-evil/secret.txt";

            _tool.TestRun(malicious, "dst.txt", overwrite: false, createDirectories: true)
                .ShouldBeError(ToolError.Codes.InvalidArgument);
        }
        finally
        {
            if (Directory.Exists(sibling))
            {
                Directory.Delete(sibling, true);
            }

        }
    }

    // The jail vets the argument path, but a symlink discovered inside the tree can point
    // anywhere — the recursive copy must not follow it, or the target's content leaks into
    // the destination (and a cycle would recurse forever).
    [Fact]
    public void Run_DirectoryContainingSymlinks_DoesNotFollowThem()
    {
        var outside = _root + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            File.WriteAllText(Path.Combine(_root, "src", "real.txt"), "real");
            Directory.CreateSymbolicLink(Path.Combine(_root, "src", "linkdir"), outside);
            File.CreateSymbolicLink(Path.Combine(_root, "src", "link.txt"), Path.Combine(outside, "secret.txt"));

            _tool.TestRun("src", "dst", overwrite: false, createDirectories: true);

            File.ReadAllText(Path.Combine(_root, "dst", "real.txt")).ShouldBe("real");
            Directory.Exists(Path.Combine(_root, "dst", "linkdir")).ShouldBeFalse();
            File.Exists(Path.Combine(_root, "dst", "link.txt")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    private class TestableCopyTool(string root) : CopyTool(root)
    {
        public JsonNode TestRun(string source, string destination, bool overwrite, bool createDirectories)
            => Run(source, destination, overwrite, createDirectories).ToNode();
    }
}