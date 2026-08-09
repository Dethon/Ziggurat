using System.Text.Json.Nodes;
using Domain.Tools;
using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class BlobReadToolTests : IDisposable
{
    private readonly string _root;
    private readonly TestableBlobReadTool _tool;

    public BlobReadToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"blobread-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
        _tool = new TestableBlobReadTool(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Run_ReadsChunkAndReportsEofWhenFullyRead()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), bytes);

        var result = _tool.TestRun("blob.bin", offset: 0, length: 200);

        var b64 = result["contentBase64"]!.GetValue<string>();
        Convert.FromBase64String(b64).ShouldBe(bytes);
        result["eof"]!.GetValue<bool>().ShouldBeTrue();
        result["totalBytes"]!.GetValue<long>().ShouldBe(100);
    }

    [Fact]
    public void Run_OffsetReadsFromMiddle()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), bytes);

        var result = _tool.TestRun("blob.bin", offset: 50, length: 30);

        var got = Convert.FromBase64String(result["contentBase64"]!.GetValue<string>());
        got.ShouldBe(bytes[50..80]);
        result["eof"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Run_MissingFile_ReturnsNotFound()
    {
        _tool.TestRun("missing.bin", 0, 100).ShouldBeError(ToolError.Codes.NotFound);
    }

    [Fact]
    public void Run_OffsetAtOrPastEnd_ReturnsEmptyContentAndEof()
    {
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), new byte[10]);

        var result = _tool.TestRun("blob.bin", offset: 10, length: 100);

        result["contentBase64"]!.GetValue<string>().ShouldBe("");
        result["eof"]!.GetValue<bool>().ShouldBeTrue();
        result["totalBytes"]!.GetValue<long>().ShouldBe(10);
    }

    [Fact]
    public void Run_NegativeLength_ReturnsInvalidArgument()
    {
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), new byte[10]);

        _tool.TestRun("blob.bin", offset: 0, length: -1).ShouldBeError(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public void Run_PathToSiblingDirectoryWithRootPrefix_ReturnsInvalidArgument()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllBytes(Path.Combine(sibling, "secret.bin"), new byte[] { 1, 2, 3 });
            var rootName = Path.GetFileName(_root);
            var malicious = $"../{rootName}-evil/secret.bin";

            _tool.TestRun(malicious, 0, 100).ShouldBeError(ToolError.Codes.InvalidArgument);
        }
        finally
        {
            if (Directory.Exists(sibling))
            {
                Directory.Delete(sibling, true);
            }
        }
    }

    private class TestableBlobReadTool(string root) : BlobReadTool(root)
    {
        public JsonNode TestRun(string path, long offset, int length)
            => Run(path, offset, length).ToNode();
    }
}