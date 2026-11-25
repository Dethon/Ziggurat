using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem.Bridge;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// What an app read makes of what it was sent and of what went wrong past it.
public class AppReadTests
{
    // A read the app spelled wrong is its own invalid argument.
    [Fact]
    public async Task ArgumentsOfTheWrongShape_AreTheCallersInvalidArgument()
    {
        var read = Read(new Throwing());

        var answer = await read.RunAsync(AppReads.Operations.Read, new JsonObject { ["filePath"] = 3 }, CancellationToken.None);

        answer.ShouldBeOfType<AppReadAnswer.Json>().Node["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
    }

    // A fault past the arguments — a backend's, a disposed client's — is never told to the app as its
    // own bad argument: it goes on as the fault it is.
    [Fact]
    public async Task AFaultPastTheArguments_IsNeverTheCallersInvalidArgument()
    {
        var read = Read(new Throwing());

        await Should.ThrowAsync<ObjectDisposedException>(() =>
            read.RunAsync(AppReads.Operations.Read, new JsonObject { ["filePath"] = "/notes/a.md" }, CancellationToken.None));
    }

    private static AppRead Read(FileSystemBackendBase backend) =>
        new(new VfsCall("", BridgeFixtures.Registry((backend, "/notes", null)), BridgeFixtures.Everything, null,
            [new FileSystemMount("notes", "/notes", "Notes.")], null, 1024), ["/notes"], []);

    private sealed class Throwing : FileSystemBackendBase
    {
        public override string FilesystemName => "notes";

        public override string DescribeMount => "Notes.";

        public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
            throw new ObjectDisposedException("the server's client");
    }
}