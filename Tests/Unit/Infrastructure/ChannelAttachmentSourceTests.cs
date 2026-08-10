using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// A reference is immutable — the upload store never rewrites a file, it only sweeps one — so the
// bytes are fetched once. Without that, the whole file crosses MCP again on every function-calling
// iteration of a turn, which for a large PDF is minutes of round trips before the model speaks.
public class ChannelAttachmentSourceTests
{
    [Fact]
    public async Task TheSameAttachment_IsFetchedOnceHoweverOftenItIsHydrated()
    {
        var channel = new CountingChannel();
        var source = new ChannelAttachmentSource([channel]);

        foreach (var _ in Enumerable.Range(0, 8))
        {
            (await source.FetchAsync("signalr", "7-42/abc", CancellationToken.None))
                .ShouldBe([1, 2, 3]);
        }

        channel.Fetches.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentHydrationsOfOneAttachment_ShareOneRoundTrip()
    {
        var channel = new CountingChannel { Gate = new TaskCompletionSource() };
        var source = new ChannelAttachmentSource([channel]);

        var fetches = Enumerable.Range(0, 5)
            .Select(_ => source.FetchAsync("signalr", "7-42/abc", CancellationToken.None))
            .ToList();
        channel.Gate.SetResult();
        await Task.WhenAll(fetches);

        channel.Fetches.ShouldBe(1);
    }

    // A file that could not be had is not cached: the next turn may find the channel back up, and
    // a cached nothing would keep answering with a placeholder for a file that is still there.
    [Fact]
    public async Task AFetchThatCameBackWithNothing_IsAskedAgainNextTime()
    {
        var channel = new CountingChannel { Bytes = null };
        var source = new ChannelAttachmentSource([channel]);

        (await source.FetchAsync("signalr", "7-42/abc", CancellationToken.None)).ShouldBeNull();
        channel.Bytes = [9];
        (await source.FetchAsync("signalr", "7-42/abc", CancellationToken.None)).ShouldBe([9]);

        channel.Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task AnAttachmentNamingAChannelThisAgentHasNoConnectionTo_ComesBackEmpty()
    {
        var source = new ChannelAttachmentSource([new CountingChannel()]);

        (await source.FetchAsync("telegram", "7-42/abc", CancellationToken.None)).ShouldBeNull();
    }

    private sealed class CountingChannel : IChannelConnection
    {
        private int _fetches;

        public int Fetches => Volatile.Read(ref _fetches);

        public byte[]? Bytes { get; set; } = [1, 2, 3];

        public TaskCompletionSource? Gate { get; init; }

        public string ChannelId => "signalr";

        public bool AttachOnly => false;

        public IAsyncEnumerable<ChannelMessage> Messages => AsyncEnumerable.Empty<ChannelMessage>();

        public async Task<byte[]?> FetchAttachmentAsync(string attachmentId, CancellationToken ct)
        {
            Interlocked.Increment(ref _fetches);
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return Bytes;
        }

        public Task SendReplyAsync(SendReplyParams reply, CancellationToken ct) => Task.CompletedTask;

        public Task<ToolApprovalResult> RequestApprovalAsync(
            string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct) =>
            Task.FromResult(new ToolApprovalResult());

        public Task NotifyAutoApprovedAsync(
            string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<string?> CreateConversationAsync(
            string agentId, string topicName, string sender, string? initialPrompt,
            string? address, string? existingConversationId, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }
}