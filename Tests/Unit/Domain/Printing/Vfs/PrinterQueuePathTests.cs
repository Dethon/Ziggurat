using Domain.Tools.Printing.Vfs;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Printing.Vfs;

public class PrinterQueuePathTests
{
    [Theory]
    [InlineData("", PrinterNodeKind.Root, null)]
    [InlineData("/", PrinterNodeKind.Root, null)]
    [InlineData("status.json", PrinterNodeKind.StatusFile, null)]
    [InlineData("/status.json", PrinterNodeKind.StatusFile, null)]
    [InlineData("report.pdf", PrinterNodeKind.DocumentFile, "report.pdf")]
    [InlineData("/report.pdf", PrinterNodeKind.DocumentFile, "report.pdf")]
    public void Parse_ClassifiesNodes(string path, PrinterNodeKind kind, string? fileName)
    {
        var node = PrinterQueuePath.Parse(path);
        node.Kind.ShouldBe(kind);
        node.FileName.ShouldBe(fileName);
    }

    [Theory]
    [InlineData("sub/report.pdf")]
    [InlineData("../escape")]
    [InlineData("./report.pdf")]
    public void Parse_RejectsNestedOrTraversalPaths(string path)
    {
        PrinterQueuePath.Parse(path).Kind.ShouldBe(PrinterNodeKind.Unknown);
    }
}