using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// Not connected is five behaviours, one per member, and they differ because their callers differ.
// They are stated on IMcpChannelConnection and pinned here, so a later tidy-up that unifies them
// fails a test rather than the delivery path.
// See docs/adr/0011-not-connected-is-five-behaviours-and-stays-that-way.md.
public class McpChannelConnectionNotConnectedTests
{
    private static McpChannelConnection NeverConnected() => new("ch-1");

    [Fact]
    public async Task SendReplyAsync_NotConnected_Throws()
    {
        // Called by an agent mid-turn, which has somewhere to report a failure.
        await Should.ThrowAsync<InvalidOperationException>(() => NeverConnected().SendReplyAsync(
            new SendReplyParams
            {
                ConversationId = "conv-1",
                Content = "hola",
                ContentType = ReplyContentType.Text,
                IsComplete = false,
                MessageId = "m-1"
            },
            CancellationToken.None));
    }

    [Fact]
    public async Task RequestApprovalAsync_NotConnected_Throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => NeverConnected().RequestApprovalAsync(
            "conv-1", [], CancellationToken.None));
    }

    [Fact]
    public async Task NotifyAutoApprovedAsync_NotConnected_Throws()
    {
        // The third send verb: same caller, same mid-turn moment, so the same behaviour. It is on
        // the throwing side of the rule and nothing may quietly move it to the silent side.
        await Should.ThrowAsync<InvalidOperationException>(() => NeverConnected().NotifyAutoApprovedAsync(
            "conv-1", [], CancellationToken.None));
    }

    [Fact]
    public async Task StoreAttachmentAsync_NotConnected_ReturnsFalse()
    {
        // A sixth member, on the silent side: its one caller copies a review's files in the
        // background and can do nothing with a failure but log it, so "not kept" is the answer.
        var kept = await NeverConnected().StoreAttachmentAsync(
            "11:22",
            new AttachmentReference { Id = "11-22/a", FileName = "a.jpg", MediaType = "image/jpeg", SizeBytes = 1 },
            [1],
            CancellationToken.None);

        kept.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateConversationAsync_NotConnected_ReturnsNull()
    {
        // The load-bearing one: DeliveryTargetResolver reads null as "this channel minted nothing",
        // which is also what an attach-only channel and a channel with no create_conversation tool
        // return. An exception would make the resolver catch in order to keep trying targets.
        var result = await NeverConnected().CreateConversationAsync(
            "agent-1", "topic", "sender", null, null, null, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task RegisterAgentsAsync_NotConnected_ReturnsSilently()
    {
        // Called by the connection's own supervision, which reacts to the answer, not an exception.
        await Should.NotThrowAsync(() => NeverConnected().RegisterAgentsAsync(
            [new AgentCatalogEntry("jonas", "Jonas", null)], CancellationToken.None));
    }

    [Fact]
    public async Task IsHealthyAsync_NotConnected_ReturnsFalse()
    {
        (await NeverConnected().IsHealthyAsync(CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Messages_NotConnected_YieldsForever()
    {
        // The agent's read loop awaits messages for the process lifetime, so a reconnect is
        // invisible to it. A completed sequence would end the loop.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in NeverConnected().Messages.WithCancellation(cts.Token))
            {
                throw new InvalidOperationException("nothing should arrive");
            }
        });
    }
}