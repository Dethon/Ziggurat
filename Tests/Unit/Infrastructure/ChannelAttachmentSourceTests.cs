using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security.Signing;
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
            (await source.FetchAsync("signalr", null, "7-42/abc", null, CancellationToken.None))
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
            .Select(_ => source.FetchAsync("signalr", null, "7-42/abc", null, CancellationToken.None))
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

        (await source.FetchAsync("signalr", null, "7-42/abc", null, CancellationToken.None)).ShouldBeNull();
        channel.Bytes = [9];
        (await source.FetchAsync("signalr", null, "7-42/abc", null, CancellationToken.None)).ShouldBe([9]);

        channel.Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task AnAttachmentNamingAChannelThisAgentHasNoConnectionTo_ComesBackEmpty()
    {
        var source = new ChannelAttachmentSource([new CountingChannel()]);

        (await source.FetchAsync("telegram", null, "7-42/abc", null, CancellationToken.None)).ShouldBeNull();
    }

    // Two households' servers of one name each keep their own files: a reference is asked of the
    // connection of the household that took it, never another's of that id.
    [Fact]
    public async Task AnAttachment_IsFetchedFromTheConnectionOfTheHouseholdThatTookIt()
    {
        var home = new CountingChannel { ChannelId = "voice", Household = ChannelHousehold.Own("home", home: true), Bytes = [1] };
        var casa = new CountingChannel { ChannelId = "voice", Household = ChannelHousehold.Own("casa", home: false), Bytes = [2] };
        var source = new ChannelAttachmentSource([home, casa]);

        (await source.FetchAsync("voice", "casa", "7-42/abc", null, CancellationToken.None)).ShouldBe([2]);
        (await source.FetchAsync("voice", "home", "7-42/abc", null, CancellationToken.None)).ShouldBe([1]);
        (await source.FetchAsync("voice", "elsewhere", "7-42/abc", null, CancellationToken.None)).ShouldBeNull();

        (home.Fetches, casa.Fetches).ShouldBe((1, 1));
    }

    // The fetch is signed as the turn asking, so the channel decides whose a file is: a file handed to one
    // household's turn is asked again for another's, never answered from what the first was given.
    [Fact]
    public async Task AFetch_IsMadeForTheTurnAsking_AndIsNeverAnotherHouseholdsCacheHit()
    {
        var channel = new CountingChannel { ChannelId = "apps" };
        var source = new ChannelAttachmentSource([channel]);
        var ours = TurnOf("household-a");
        var theirs = TurnOf("household-b");

        await source.FetchAsync("apps", null, "7-42/abc", ours, CancellationToken.None);
        await source.FetchAsync("apps", null, "7-42/abc", ours, CancellationToken.None);
        await source.FetchAsync("apps", null, "7-42/abc", theirs, CancellationToken.None);

        channel.Turns.ShouldBe([ours, theirs]);
    }

    // A channel may keep a file for one conversation alone (an app's ask), so a turn of another conversation of
    // the same household is asked of the channel too, never answered from the first one's fetch.
    [Fact]
    public async Task AFetch_IsNeverAnotherConversationsCacheHit()
    {
        var channel = new CountingChannel { ChannelId = "apps" };
        var source = new ChannelAttachmentSource([channel]);
        var ours = TurnOf("household-a");
        var another = TurnOf("household-a", "7:43");

        await source.FetchAsync("apps", null, "7-42/abc", ours, CancellationToken.None);
        await source.FetchAsync("apps", null, "7-42/abc", another, CancellationToken.None);

        channel.Turns.ShouldBe([ours, another]);
    }

    private static ConversationContext TurnOf(string household, string conversation = "7:42") =>
        new("jonas", conversation, "user", new ReplyTarget("apps", conversation))
        {
            Identity = IdentityClaims.Person("user", household, "apps", [])
        };

    private sealed class CountingChannel : IChannelConnection
    {
        public ChannelHousehold Household { get; init; } = ChannelHousehold.Every;

        private int _fetches;

        public int Fetches => Volatile.Read(ref _fetches);

        public byte[]? Bytes { get; set; } = [1, 2, 3];

        public TaskCompletionSource? Gate { get; init; }

        public string ChannelId { get; init; } = "signalr";

        public bool AttachOnly => false;

        public bool HostsReviewCopies => false;
        public bool ShowsReasoning => true;
        public bool ShowsToolCalls => false;

        public bool IsConnected => true;

        public IAsyncEnumerable<ChannelMessage> Messages => AsyncEnumerable.Empty<ChannelMessage>();

        private readonly System.Collections.Concurrent.ConcurrentQueue<ConversationContext?> _turns = new();

        public IReadOnlyList<ConversationContext?> Turns => [.. _turns];

        public async Task<byte[]?> FetchAttachmentAsync(string attachmentId, ConversationContext? turn, CancellationToken ct)
        {
            Interlocked.Increment(ref _fetches);
            _turns.Enqueue(turn);
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return Bytes;
        }

        public Task<ReplyReceipt> SendReplyAsync(SendReplyParams reply, CancellationToken ct) => Task.FromResult(ReplyReceipt.None);

        public Task<bool> StoreAttachmentAsync(
            string conversationId, AttachmentReference attachment, byte[] bytes, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<ToolApprovalResult?> AskApprovalAsync(ApprovalAsk ask, CancellationToken ct) =>
            Task.FromResult<ToolApprovalResult?>(null);

        public Task<ToolApprovalResult?> OfferApprovalAsync(ApprovalAsk ask, CancellationToken ct) =>
            Task.FromResult<ToolApprovalResult?>(null);

        public Task SettleApprovalAsync(string approvalId, ToolApprovalResult result, CancellationToken ct) =>
            Task.CompletedTask;

        public Task NotifyAutoApprovedAsync(
            string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<string?> CreateConversationAsync(
            string agentId, string topicName, string sender, string? initialPrompt,
            string? address, string? existingConversationId, Principal? principal, bool group, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }
}