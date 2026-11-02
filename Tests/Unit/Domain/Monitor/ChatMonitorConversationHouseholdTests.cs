using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.WebChat;
using Domain.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A conversation is its household's: a household's own server naming another household's conversation
// — live, or kept under a topic — is refused before it joins that conversation or reads its history.
public class ChatMonitorConversationHouseholdTests
{
    private static readonly Account _fran = new("home", "0123456789abcdef0123456789abcde0");
    private static readonly Account _lucia = new("casa", "0123456789abcdef0123456789abcde1");

    [Fact]
    public async Task AMessageNamingAnotherHouseholdsLiveConversation_IsRefused_AndNeverJoinsIt()
    {
        var homeVoice = Own("home", home: true);
        var casaVoice = Own("casa", home: false);
        homeVoice.WriteMessage(From(_fran, "conv-1"));
        casaVoice.WriteMessage(From(_lucia, "conv-1"));
        var (factory, errors) = await RunAsync(MonitorTestMocks.CreateThreadResolver(), homeVoice, casaVoice);

        factory.Created.ShouldHaveSingleItem().TenantId.ShouldBe("home");
        errors.ShouldContain(e => e.ErrorType == ChatMonitor.WrongHouseholdErrorType && e.Message!.Contains("casa") && e.Message.Contains("home"));
        casaVoice.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.Error);
    }

    [Fact]
    public async Task AMessageNamingAConversationAnotherHouseholdsTopicHolds_IsRefused_BeforeItsHistoryIsRead()
    {
        var store = new Mock<IThreadStateStore>();
        store.Setup(s => s.GetTopicByChatIdAndThreadIdAsync("jonas", 7, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TopicMetadata("t-1", 7, 42, "jonas", "Kitchen", DateTimeOffset.UtcNow, null, TenantId: "home"));
        var casaVoice = Own("casa", home: false);
        casaVoice.WriteMessage(From(_lucia, "7:42"));

        var (factory, errors) = await RunAsync(new ChatThreadResolver(store.Object), casaVoice);

        factory.Created.ShouldBeEmpty();
        errors.ShouldContain(e => e.ErrorType == ChatMonitor.WrongHouseholdErrorType);
    }

    [Fact]
    public async Task AMessageNamingItsOwnHouseholdsTopic_IsAnswered()
    {
        var store = new Mock<IThreadStateStore>();
        store.Setup(s => s.GetTopicByChatIdAndThreadIdAsync("jonas", 7, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TopicMetadata("t-1", 7, 42, "jonas", "Kitchen", DateTimeOffset.UtcNow, null, TenantId: "casa"));
        var casaVoice = Own("casa", home: false);
        casaVoice.WriteMessage(From(_lucia, "7:42"));

        var (factory, _) = await RunAsync(new ChatThreadResolver(store.Object), casaVoice);

        factory.Created.ShouldHaveSingleItem().TenantId.ShouldBe("casa");
    }

    // A household's schedule whose delegation no longer holds runs as nobody, but it is still that
    // household's: its own agent answers it and it is spoken on its own satellites, never the home's.
    [Fact]
    public async Task AnUndelegatedFireOfAHousehold_IsThatHouseholds_AndDeliversToItsOwnVoice()
    {
        var scheduling = new FakeChannelConnection { ChannelId = "scheduling" };
        var homeVoice = new FakeChannelConnection
        {
            ChannelId = "voice", AttachOnly = true, Household = ChannelHousehold.Own("home", home: true), ConversationIdToReturn = "home-c"
        };
        var casaVoice = new FakeChannelConnection
        {
            ChannelId = "voice", AttachOnly = true, Household = ChannelHousehold.Own("casa", home: false), ConversationIdToReturn = "casa-c"
        };
        scheduling.WriteMessage(new ChannelMessage
        {
            ConversationId = "sched-1",
            Content = "Recuérdame sacar la pasta",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            HouseholdId = "casa",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "s-1"),
            ReplyTo = [new ReplyTarget("voice", null)]
        });

        var (factory, _) = await RunAsync(MonitorTestMocks.CreateThreadResolver(), scheduling, homeVoice, casaVoice);

        factory.Created.ShouldHaveSingleItem().TenantId.ShouldBe("casa");
        casaVoice.CreatedConversations.ShouldNotBeEmpty();
        homeVoice.CreatedConversations.ShouldBeEmpty();
    }

    // What a household's own server sends that is refused is counted under that household, so one
    // household's misbehaving server does not show as the home's.
    [Fact]
    public async Task AnAssertionRefusedOnAHouseholdsOwnServer_IsCountedUnderThatHousehold()
    {
        var casaVoice = Own("casa", home: false);
        casaVoice.WriteUnsigned(From(_lucia, "conv-1"));

        var (_, errors) = await RunAsync(MonitorTestMocks.CreateThreadResolver(), casaVoice);

        errors.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.ErrorType.ShouldBe(ChatMonitor.AssertionRefusedErrorType),
            e => e.TenantId.ShouldBe("casa"));
    }

    private static FakeChannelConnection Own(string household, bool home) =>
        new() { ChannelId = "voice", Household = ChannelHousehold.Own(household, home) };

    private static ChannelMessage From(Account person, string conversation) =>
        MonitorTestMocks.CreateChannelMessage(conversationId: conversation, channelId: "voice", agentId: "jonas") with
        {
            Principal = new Principal("voice", person.UserId, person)
        };

    private static async Task<(FakeAgentFactory Factory, IReadOnlyList<ErrorEvent> Errors)> RunAsync(
        ChatThreadResolver threads, params FakeChannelConnection[] channels)
    {
        foreach (var channel in channels)
        {
            channel.Complete();
        }

        var factory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());
        var metrics = new RecordingMetricsPublisher();
        await new ChatMonitor(
            ConnectionSet.Of<IChannelConnection>(channels), factory, threads, metrics, null,
            NullLogger<ChatMonitor>.Instance, TestAssertions.Verifier()).Monitor(CancellationToken.None);
        return (factory, [.. metrics.Published.OfType<ErrorEvent>()]);
    }
}