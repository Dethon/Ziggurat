using Domain.Conversations;
using Domain.DTOs.Channel;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceConversationManagerTests
{
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(5);

    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static (VoiceConversationManager Sut, FakeTopicMinter Topics) Build(FakeTimeProvider clock)
    {
        var topics = new FakeTopicMinter();
        var sut = new VoiceConversationManager(
            topics, new ReplyTextAccumulator(), clock, _lifetime,
            NullLogger<VoiceConversationManager>.Instance);
        return (sut, topics);
    }

    [Fact]
    public async Task FirstUtterance_MintsConversation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, topics) = Build(clock);

        var id = await sut.GetOrCreateAsync(Session(), "agent-1", default);

        id.ShouldNotBeNullOrWhiteSpace();
        sut.GetActiveConversationId("kitchen-01").ShouldBe(id);
        sut.ResolveSatelliteId(id).ShouldBe("kitchen-01");
        topics.Minted.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SecondUtteranceWithinWindow_ReusesAndRenews()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, topics) = Build(clock);

        var first = await sut.GetOrCreateAsync(Session(), "agent-1", default);
        clock.Advance(TimeSpan.FromMinutes(4));
        var second = await sut.GetOrCreateAsync(Session(), "agent-1", default);

        second.ShouldBe(first);
        topics.Minted.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromMinutes(4));
        sut.GetActiveConversationId("kitchen-01").ShouldBe(first);
    }

    [Fact]
    public async Task AfterIdleExpiry_NextUtteranceMintsNewConversation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);

        var first = await sut.GetOrCreateAsync(Session(), "agent-1", default);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        sut.GetActiveConversationId("kitchen-01").ShouldBeNull();
        sut.ResolveSatelliteId(first).ShouldBeNull();

        var second = await sut.GetOrCreateAsync(Session(), "agent-1", default);
        second.ShouldNotBe(first);
        sut.ResolveSatelliteId(second).ShouldBe("kitchen-01");
    }

    [Fact]
    public async Task BuildsTopicNameFromIdentityAndRoom()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, topics) = Build(clock);

        await sut.GetOrCreateAsync(Session(), "agent-1", default);

        topics.Minted.ShouldHaveSingleItem().Request
            .ShouldBe(new TopicMintRequest("agent-1", "household @ Kitchen", "household"));
    }

    // Whose the topic is, is the agent's to decide from the accounts service's word for who spoke,
    // so the mint carries the assertion the speaker came with, under the name they go by.
    [Fact]
    public async Task AMintedConversation_CarriesTheSpeakersAssertionToTheAgent()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, topics) = Build(clock);

        var id = await sut.GetOrCreateAsync(
            Session(), "agent-1", default, Task.FromResult<(string, string?)>(("Fran", "fran's assertion")));

        var (request, assertion) = topics.Minted.ShouldHaveSingleItem();
        (request.Sender, assertion).ShouldBe(("Fran", "fran's assertion"));
        id.ShouldBe(ConversationIdGenerator.CreateFor("topic-1").ConversationId);
    }

    [Fact]
    public async Task TransferBinding_ActiveConversation_MovesToTargetSatellite()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);
        var sessionA = new SatelliteSession("sat-a", new SatelliteConfig { Identity = "household", Room = "Office A" });
        var conversationId = await sut.GetOrCreateAsync(sessionA, "agent-1", default);

        clock.Advance(TimeSpan.FromMinutes(2));
        sut.TransferBinding("sat-a", "sat-b", clock.GetTimestamp()).ShouldBe(BindingTransfer.Moved);

        sut.GetActiveConversationId("sat-b").ShouldBe(conversationId);
        sut.GetActiveConversationId("sat-a").ShouldBeNull();
        sut.ResolveSatelliteId(conversationId).ShouldBe("sat-b");

        // A late-firing timer tied to sat-a's original creation (due at t=5min) must not resurrect
        // or clear anything now that the conversation lives under sat-b with its own fresh timer
        // (due at t=2min+5min=7min).
        clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        sut.GetActiveConversationId("sat-b").ShouldBe(conversationId);
    }

    [Fact]
    public void TransferBinding_NoActiveConversation_ReportsNothingToMove()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);

        sut.TransferBinding("sat-a", "sat-b", clock.GetTimestamp()).ShouldBe(BindingTransfer.NothingToMove);
    }

    [Fact]
    public async Task TransferBinding_TargetHadItsOwnConversation_TargetEntryIsDisplaced()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);
        var sessionA = new SatelliteSession("sat-a", new SatelliteConfig { Identity = "household", Room = "Office A" });
        var sessionB = new SatelliteSession("sat-b", new SatelliteConfig { Identity = "household", Room = "Office B" });
        var conversationA = await sut.GetOrCreateAsync(sessionA, "agent-1", default);
        var conversationB = await sut.GetOrCreateAsync(sessionB, "agent-1", default);

        clock.Advance(TimeSpan.FromMinutes(2));
        sut.TransferBinding("sat-a", "sat-b", clock.GetTimestamp()).ShouldBe(BindingTransfer.Moved);

        sut.GetActiveConversationId("sat-b").ShouldBe(conversationA);
        sut.ResolveSatelliteId(conversationB).ShouldBeNull();

        // conversationB's original idle timer (due at t=5min from its own creation) must not
        // resurrect it or clear the slot out from under the transferred conversation, which now
        // has its own fresh timer due later (transfer happened at t=2min, so due at t=7min).
        clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        sut.GetActiveConversationId("sat-b").ShouldBe(conversationA);
        sut.ResolveSatelliteId(conversationB).ShouldBeNull();
    }

    [Fact]
    public async Task TransferBinding_TargetBoundAfterClaim_SkipsTheStaleHandoff()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);
        var sessionA = new SatelliteSession("sat-a", new SatelliteConfig { Identity = "household", Room = "Office A" });
        var sessionB = new SatelliteSession("sat-b", new SatelliteConfig { Identity = "household", Room = "Office B" });
        var conversationA = await sut.GetOrCreateAsync(sessionA, "agent-1", default);

        clock.Advance(TimeSpan.FromMinutes(1));
        var claimedAt = clock.GetTimestamp(); // sat-b's wake claim arrives
        clock.Advance(TimeSpan.FromSeconds(2)); // decision delayed (e.g. a wedged loser's re-arm)
        var conversationB = await sut.GetOrCreateAsync(sessionB, "agent-1", default);

        // sat-b's own turn already ran and bound its own conversation after the claim: the
        // handoff is stale, and displacing conversationB would silently drop its in-flight reply.
        sut.TransferBinding("sat-a", "sat-b", claimedAt).ShouldBe(BindingTransfer.DeclinedStale);

        sut.ResolveSatelliteId(conversationB).ShouldBe("sat-b");
        sut.GetActiveConversationId("sat-b").ShouldBe(conversationB);
        sut.GetActiveConversationId("sat-a").ShouldBe(conversationA);
    }
}