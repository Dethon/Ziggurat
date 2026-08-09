using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Extensions;
using Domain.Monitor;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

// Where an agent has a sandbox, an attachment is not only something the model can look at but a
// real file it can work on. The upload store is never mounted, so the sandbox is the only
// filesystem where an attachment appears as a file to the model (ADR 0021).
public class ChatMonitorSandboxLandingTests
{
    private static readonly AttachmentReference _photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    private static readonly AttachmentReference _sameNameAgain = new()
    {
        Id = "7-42/def",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    [Fact]
    public async Task EachAttachment_IsWrittenUnderAPerConversationPerMessageDirectory()
    {
        var sandbox = new RecordingSandbox();
        var agent = AgentWith(sandbox);

        await RunAsync(agent, _photo);

        var written = sandbox.Writes.ShouldHaveSingleItem();
        written.Path.ShouldEndWith("/photo.png");
        written.Path.ShouldContain("uploads/7-42/");
        written.Bytes.ShouldBe([1, 2, 3, 4]);
    }

    // Recorded on the message rather than written into its text: hydration is what names the
    // path to the model, so the transcript a person reads never grows an internal path.
    [Fact]
    public async Task TheMessage_CarriesTheVirtualPathSoTheModelCanActOnItUnprompted()
    {
        var sandbox = new RecordingSandbox();
        var agent = AgentWith(sandbox);

        await RunAsync(agent, _photo);

        agent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        var message = messages!.Single();
        var path = message.GetSandboxPaths().ShouldHaveSingleItem();
        path.ShouldStartWith("/sandbox/uploads/7-42/");
        path.ShouldEndWith("/photo.png");

        string.Join("", message.Contents.OfType<TextContent>().Select(c => c.Text))
            .ShouldNotContain("/sandbox/");
    }

    // The per-message directory is what removes collisions: sending scan.pdf twice in one
    // conversation does not lose the first one, and nothing is renamed.
    [Fact]
    public async Task TwoFilesWithTheSameNameInOneConversation_BothSurvive()
    {
        var sandbox = new RecordingSandbox();
        var agent = AgentWith(sandbox);

        await RunAsync(agent, [_photo], [_sameNameAgain]);

        sandbox.Writes.Count.ShouldBe(2);
        sandbox.Writes.Select(w => w.Path).Distinct().Count().ShouldBe(2);
        sandbox.Writes.ShouldAllBe(w => w.Path.EndsWith("/photo.png"));
    }

    // The per-message directory separates one message's files from another's. Within one message
    // two files can still share a name, and there the second gets a directory of its own rather
    // than overwriting the first — the model must not be told two files exist where one does.
    [Fact]
    public async Task TwoFilesWithTheSameNameInOneMessage_BothSurviveAndAreNamedApart()
    {
        var sandbox = new RecordingSandbox();
        var agent = AgentWith(sandbox);

        await RunAsync(agent, _photo, _sameNameAgain);

        sandbox.Writes.Count.ShouldBe(2);
        sandbox.Writes.Select(w => w.Path).Distinct().Count().ShouldBe(2);
        sandbox.Writes.ShouldAllBe(w => w.Path.EndsWith("/photo.png"));

        agent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        messages!.Single().GetSandboxPaths()!.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task AnAgentWithNoSandbox_StillGetsTheAttachmentAsModelContext()
    {
        var agent = AgentWith(sandbox: null);

        await RunAsync(agent, _photo);

        agent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        var message = messages!.Single();
        message.GetAttachments().ShouldBe([_photo]);
        message.GetSandboxPaths().ShouldBeNull();
    }

    [Fact]
    public async Task AFailedSandboxWrite_LeavesTheTurnIntactAndTheAttachmentStillReachesTheModel()
    {
        var sandbox = new RecordingSandbox { Throws = true };
        var agent = AgentWith(sandbox);

        await RunAsync(agent, _photo);

        agent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        var message = messages!.Single();
        message.GetAttachments().ShouldBe([_photo]);
        message.GetSandboxPaths().ShouldBeNull();
    }

    private static FakeAiAgent AgentWith(RecordingSandbox? sandbox)
    {
        var agent = MonitorTestMocks.CreateAgent();
        if (sandbox is not null)
        {
            var registry = new StubRegistry(sandbox);
            agent.FileSystemRegistry = registry;
        }

        return agent;
    }

    private static Task RunAsync(FakeAiAgent agent, params AttachmentReference[] attachments)
        => RunAsync(agent, [attachments]);

    private static async Task RunAsync(
        FakeAiAgent agent, params IReadOnlyList<AttachmentReference>[] messageAttachments)
    {
        var messages = messageAttachments
            .Select(attachments => MonitorTestMocks.CreateChannelMessage(
                conversationId: "7:42", channelId: "signalr", agentId: "jonas") with
            {
                Attachments = attachments
            })
            .ToArray();
        var channel = MonitorTestMocks.CreateChannel("signalr", messages);
        foreach (var attachment in messageAttachments.SelectMany(a => a))
        {
            channel.Attachments[attachment.Id] = [1, 2, 3, 4];
        }

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);
    }

    private sealed class StubRegistry(RecordingSandbox sandbox) : IVirtualFileSystemRegistry
    {
        public void Mount(FileSystemMount mount, IFileSystemBackend backend) { }

        public FsResult<FileSystemResolution> Resolve(string virtualPath)
        {
            const string mountPoint = "/sandbox";
            return virtualPath.StartsWith(mountPoint, StringComparison.Ordinal)
                ? new FsResult<FileSystemResolution>.Ok(
                    new FileSystemResolution(sandbox, virtualPath[mountPoint.Length..], mountPoint))
                : FsError.NotFound<FileSystemResolution>(virtualPath);
        }

        public IReadOnlyList<FileSystemMount> GetMounts() =>
        [
            new FileSystemMount("sandbox", "/sandbox", "a sandbox")
            {
                Capabilities = [VfsExecTool.Name]
            }
        ];
    }

    private sealed class RecordingSandbox : FileSystemBackendBase
    {
        public bool Throws { get; init; }

        public List<(string Path, byte[] Bytes)> Writes { get; } = [];

        public override string FilesystemName => "sandbox";

        public override string DescribeMount => "a sandbox";

        public override async Task<long> WriteChunksAsync(
            string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
            bool overwrite, bool createDirectories, CancellationToken ct)
        {
            if (Throws)
            {
                throw new IOException("the sandbox is not there");
            }

            var bytes = new List<byte>();
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                bytes.AddRange(chunk.ToArray());
            }

            Writes.Add((path, bytes.ToArray()));
            return bytes.Count;
        }
    }
}