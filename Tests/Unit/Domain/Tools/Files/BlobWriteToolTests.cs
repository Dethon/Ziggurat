using System.Text.Json.Nodes;
using Domain.Tools;
using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class BlobWriteToolTests : IDisposable
{
    private readonly string _root;
    private readonly TestableBlobWriteTool _tool;

    public BlobWriteToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"blobwrite-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
        _tool = new TestableBlobWriteTool(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Run_FirstChunkCreatesFile()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var b64 = Convert.ToBase64String(data);

        var result = _tool.TestRun("out.bin", b64, offset: 0, overwrite: false, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(data);
        result["bytesWritten"]!.GetValue<int>().ShouldBe(4);
        result["totalBytes"]!.GetValue<long>().ShouldBe(4);
    }

    [Fact]
    public void Run_AppendsAtOffset()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), new byte[] { 1, 2 });
        var b64 = Convert.ToBase64String(new byte[] { 3, 4 });

        var result = _tool.TestRun("out.bin", b64, offset: 2, overwrite: true, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(new byte[] { 1, 2, 3, 4 });
        result["totalBytes"]!.GetValue<long>().ShouldBe(4);
    }

    [Fact]
    public void Run_OffsetZeroOverwriteFalseAndExists_ReturnsAlreadyExists()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), new byte[] { 1 });
        var b64 = Convert.ToBase64String(new byte[] { 9 });

        _tool.TestRun("out.bin", b64, offset: 0, overwrite: false, createDirectories: true)
            .ShouldBeError(ToolError.Codes.AlreadyExists);
    }

    [Fact]
    public void Run_OffsetZeroOverwriteTrueAndExists_Replaces()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), new byte[] { 1, 2, 3 });
        var b64 = Convert.ToBase64String(new byte[] { 9 });

        _tool.TestRun("out.bin", b64, offset: 0, overwrite: true, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(new byte[] { 9 });
    }

    [Fact]
    public void Run_CreatesParentDirectories()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1 });

        _tool.TestRun("nested/dir/out.bin", b64, offset: 0, overwrite: false, createDirectories: true);

        File.Exists(Path.Combine(_root, "nested", "dir", "out.bin")).ShouldBeTrue();
    }

    [Fact]
    public void Run_PathToSiblingDirectoryWithRootPrefix_ReturnsInvalidArgument()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var rootName = Path.GetFileName(_root);
            var malicious = $"../{rootName}-evil/out.bin";
            var b64 = Convert.ToBase64String(new byte[] { 1 });

            _tool.TestRun(malicious, b64, offset: 0, overwrite: false, createDirectories: true)
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

    [Fact]
    public void Run_NegativeOffset_ReturnsInvalidArgument()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1 });

        _tool.TestRun("out.bin", b64, offset: -1, overwrite: false, createDirectories: true)
            .ShouldBeError(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public void Run_EmptyContent_CreatesEmptyFile()
    {
        var result = _tool.TestRun("empty.bin", contentBase64: "", offset: 0,
            overwrite: false, createDirectories: true);

        File.Exists(Path.Combine(_root, "empty.bin")).ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(_root, "empty.bin")).Length.ShouldBe(0);
        result["bytesWritten"]!.GetValue<int>().ShouldBe(0);
        result["totalBytes"]!.GetValue<long>().ShouldBe(0);
    }

    // A chunked upload with overwrite=false is the normal cross-filesystem copy: the guard's
    // decision at offset 0 governs, and each later chunk continues exactly at the current end.
    [Fact]
    public void Run_ContinuationChunksWithoutOverwrite_AppendAtTheCurrentEnd()
    {
        _tool.TestRun("multi.bin", Convert.ToBase64String([1, 2, 3]), offset: 0,
            overwrite: false, createDirectories: true);
        var last = _tool.TestRun("multi.bin", Convert.ToBase64String([4, 5]), offset: 3,
            overwrite: false, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "multi.bin")).ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
        last["totalBytes"]!.GetValue<long>().ShouldBe(5);
    }

    // A cold nonzero-offset call is not a continuation of anything: the file's end is not at the
    // offset, so with overwrite=false it must not touch the existing file.
    [Fact]
    public void Run_NonzeroOffsetIntoAnExistingFileWithoutOverwrite_RefusesAndLeavesTheFileAlone()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), [1, 2, 3, 4]);

        _tool.TestRun("out.bin", Convert.ToBase64String([9]), offset: 2,
                overwrite: false, createDirectories: true)
            .ShouldBeError(ToolError.Codes.InvalidArgument);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void Run_NonzeroOffsetIntoAMissingFileWithoutOverwrite_Refuses()
    {
        _tool.TestRun("missing.bin", Convert.ToBase64String([9]), offset: 2,
                overwrite: false, createDirectories: true)
            .ShouldBeError(ToolError.Codes.InvalidArgument);

        File.Exists(Path.Combine(_root, "missing.bin")).ShouldBeFalse();
    }

    private class TestableBlobWriteTool(string root) : BlobWriteTool(root)
    {
        public JsonNode TestRun(string path, string contentBase64, long offset, bool overwrite, bool createDirectories)
            => Run(path, contentBase64, offset, overwrite, createDirectories).ToNode();
    }
}