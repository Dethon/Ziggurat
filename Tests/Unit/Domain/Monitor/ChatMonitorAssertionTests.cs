using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Domain.Monitor;
using Domain.Security;
using Domain.Security.Signing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A person's turn acts for whoever the accounts service vouched for, never for whoever the channel
// server says: the agent checks the assertion the turn arrived with — signed by the accounts service,
// in date, for the channel it came on, a person or the household's guest — as it arrives, refuses one
// that fails and says so on the errors feed and to the channel, and holds what it verified for the
// whole turn. A fire carries none (ChatMonitorDelegationTests), and a chat command is no turn.
public class ChatMonitorAssertionTests
{
    private const string ChatChannel = "signalr";
    private const string Household = "fedcba9876543210fedcba9876543210";

    private static readonly Account _fran = new(Household, "0123456789abcdef0123456789abcdef");
    private static readonly Account _mallory = new(Household, "badbadbadbadbadbadbadbadbadbad00");

    [Fact]
    public async Task APersonsTurn_ActsForTheAccountItsAssertionNames_NotTheChannelsPrincipal()
    {
        var run = new Run();
        run.Channel.WriteMessage(Message() with
        {
            Principal = new Principal(ChatChannel, _mallory.UserId, _mallory),
            Assertion = TestAssertions.Of(_fran, ChatChannel)
        });

        await run.MonitorAsync();

        run.Factory.Created.ShouldHaveSingleItem().Account.ShouldBe(_fran);
        var context = run.TurnContext();
        context.Principal.ShouldNotBeNull().Account.ShouldBe(_fran);
        var identity = context.Identity.ShouldNotBeNull();
        identity.Subject.ShouldBe(_fran.UserId);
        identity.TenantId.ShouldBe(Household);
        identity.ChannelId.ShouldBe(ChatChannel);
    }

    [Fact]
    public async Task AGuestsTurn_ActsAsTheHouseholdsGuest_ForNoAccount()
    {
        var run = new Run("voice");
        run.Channel.WriteMessage(Message("voice") with
        {
            Principal = new Principal("voice", "household"),
            Assertion = TestAssertions.OfGuest(Household, "voice")
        });

        await run.MonitorAsync();

        run.Factory.Created.ShouldHaveSingleItem().Account.ShouldBeNull();
        var context = run.TurnContext();
        context.Principal.ShouldNotBeNull().Account.ShouldBeNull();
        var identity = context.Identity.ShouldNotBeNull();
        identity.IsGuest.ShouldBeTrue();
        identity.TenantId.ShouldBe(Household);
    }

    // A household's own servers are reached in the household the turn is for, and the guest has one
    // as much as a person does — a satellite's guest still runs the home's lights.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATurnsAgent_IsBuiltForTheHouseholdItsAssertionNames(bool guest)
    {
        var run = new Run("voice");
        run.Channel.WriteMessage(Message("voice") with
        {
            Principal = new Principal("voice", "household"),
            Assertion = guest ? TestAssertions.OfGuest(Household, "voice") : TestAssertions.Of(_fran, "voice")
        });

        await run.MonitorAsync();

        run.Factory.Created.ShouldHaveSingleItem().TenantId.ShouldBe(Household);
    }

    // What the turn cost the household in time is its own: the dashboard reads the first reply's
    // latency per household as it reads the agent's tokens.
    [Fact]
    public async Task ATurnsFirstReplyLatency_CountsAgainstTheHouseholdItsAssertionNames()
    {
        var run = new Run(replies: [new AgentResponseUpdate { Contents = [new TextContent("hola")] }]);
        run.Channel.WriteMessage(Message() with
        {
            Principal = new Principal(ChatChannel, _fran.UserId, _fran),
            Assertion = TestAssertions.Of(_fran, ChatChannel)
        });

        await run.MonitorAsync();

        run.Published.OfType<LatencyEvent>().Where(e => e.Stage == LatencyStage.FirstReply)
            .ShouldHaveSingleItem().TenantId.ShouldBe(Household);
    }

    public static TheoryData<string, string?> Unbelievable => new()
    {
        { "Missing", null },
        { "Expired", TestAssertions.Of(_fran, ChatChannel, now: DateTimeOffset.UtcNow.AddMinutes(-11), lifetime: TimeSpan.FromMinutes(10)) },
        { TurnAdmission.WrongChannel, TestAssertions.Of(_fran, "telegram") },
        { "UnknownKey", TestAssertions.Forged(_fran, ChatChannel) },
        { TurnAdmission.NamesNobody, TestAssertions.OfNobody(ChatChannel) }
    };

    [Theory]
    [MemberData(nameof(Unbelievable))]
    public async Task ATurnWhoseAssertionFails_IsRefusedReportedAndAnswered(string reason, string? assertion)
    {
        var run = new Run();
        run.Channel.WriteUnsigned(Message() with
        {
            Principal = new Principal(ChatChannel, _fran.UserId, _fran),
            Assertion = assertion,
            TurnKey = "turn-7"
        });

        await run.MonitorAsync();

        run.Factory.Created.ShouldBeEmpty();
        run.Agent.ReceivedMessages.ShouldBeEmpty();
        var refused = run.Errors.ShouldHaveSingleItem();
        refused.ErrorType.ShouldBe(ChatMonitor.AssertionRefusedErrorType);
        refused.Message.ShouldNotBeNull().ShouldContain(reason);
        refused.Message.ShouldContain(ChatChannel);
        // Answered as a failed turn ends, so whoever waits on the channel is not left waiting.
        run.Channel.SentReplies.Select(r => r.ContentType)
            .ShouldBe([ReplyContentType.Error, ReplyContentType.StreamComplete]);
        run.Channel.SentReplies.ShouldAllBe(r => r.TurnKey == "turn-7" && r.ConversationId == "conv-1");
    }

    // A household of its own that set no OpenRouter key pays for nothing: the person is told so on the
    // channel, the way a failed turn ends, the errors feed says whose, and nothing is asked of its agent.
    [Fact]
    public async Task ATurnOfAHouseholdWithNoKey_IsRefusedReportedAndAnswered()
    {
        var run = new Run();
        run.Factory.Refusal = new HouseholdWithoutKeyException(Household);
        run.Channel.WriteMessage(Message() with
        {
            Principal = new Principal(ChatChannel, _fran.UserId, _fran),
            Assertion = TestAssertions.Of(_fran, ChatChannel),
            TurnKey = "turn-7"
        });

        await run.MonitorAsync();

        run.Agent.ReceivedMessages.ShouldBeEmpty();
        var refused = run.Errors.ShouldHaveSingleItem();
        refused.ErrorType.ShouldBe(ChatMonitor.HouseholdWithoutKeyErrorType);
        refused.TenantId.ShouldBe(Household);
        run.Channel.SentReplies.Select(r => (r.ContentType, r.Content))
            .ShouldBe([(ReplyContentType.Error, ConversationGroup.WithoutKeyAnswer), (ReplyContentType.StreamComplete, "")]);
        run.Channel.SentReplies.ShouldAllBe(r => r.TurnKey == "turn-7" && r.ConversationId == "conv-1");
    }

    // An assertion signed before an owner disabled its person still verifies — Telegram keeps the one
    // that admitted a sender for minutes — so the agent asks the household too, and acts for nobody
    // the household disabled: the person is told, the errors feed says whose, and no agent is built.
    // On a satellite as anywhere: voice speaks the answer instead of waiting out its reply timeout.
    [Theory]
    [InlineData(ChatChannel)]
    [InlineData("voice")]
    public async Task ADisabledMembersTurn_IsRefusedReportedAndAnswered(string channel)
    {
        var run = new Run(channel, standing: new FakeAccountStanding(_fran));
        run.Channel.WriteMessage(Message(channel) with
        {
            Principal = new Principal(channel, _fran.UserId, _fran),
            Assertion = TestAssertions.Of(_fran, channel),
            TurnKey = "turn-7"
        });

        await run.MonitorAsync();

        run.Factory.Created.ShouldBeEmpty();
        run.Agent.ReceivedMessages.ShouldBeEmpty();
        var refused = run.Errors.ShouldHaveSingleItem();
        refused.ErrorType.ShouldBe(ChatMonitor.AccountDisabledErrorType);
        refused.TenantId.ShouldBe(Household);
        refused.Message.ShouldNotBeNull().ShouldContain(_fran.UserId);
        run.Channel.SentReplies.Select(r => (r.ContentType, r.Content))
            .ShouldBe([(ReplyContentType.Error, ConversationGroup.DisabledAnswer), (ReplyContentType.StreamComplete, "")]);
        run.Channel.SentReplies.ShouldAllBe(r => r.TurnKey == "turn-7" && r.ConversationId == "conv-1");
    }

    // A standing the agent cannot read is never good standing — the turn could not be built without
    // the same view anyway — and the person is told it could not be checked, not that it was refused.
    [Fact]
    public async Task ATurnWhosePersonsStandingCannotBeRead_IsRefusedAsUnchecked()
    {
        var run = new Run(standing: new FakeAccountStanding { Unreadable = true });
        run.Channel.WriteMessage(Message() with { Assertion = TestAssertions.Of(_fran, ChatChannel) });

        await run.MonitorAsync();

        run.Factory.Created.ShouldBeEmpty();
        var refused = run.Errors.ShouldHaveSingleItem();
        refused.ErrorType.ShouldBe(ChatMonitor.StandingUnreadableErrorType);
        refused.TenantId.ShouldBe(Household);
        run.Channel.SentReplies.Select(r => (r.ContentType, r.Content))
            .ShouldBe([(ReplyContentType.Error, ConversationGroup.UncheckedAnswer), (ReplyContentType.StreamComplete, "")]);
    }

    // Whether the household is suspended is read the same way, and refused the same way when it cannot
    // be: neither let through as running nor told it is suspended, which it may not be.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATurnWhoseHouseholdCannotBeRead_IsRefusedAsUnchecked_NeverAsSuspended(bool guest)
    {
        var run = new Run("voice", households: MonitorTestMocks.UnreadableHouseholds());
        run.Channel.WriteMessage(Message("voice") with
        {
            Assertion = guest ? TestAssertions.OfGuest(Household, "voice") : TestAssertions.Of(_fran, "voice")
        });

        await run.MonitorAsync();

        run.Factory.Created.ShouldBeEmpty();
        run.Errors.ShouldHaveSingleItem().ErrorType.ShouldBe(ChatMonitor.StandingUnreadableErrorType);
        run.Channel.SentReplies.Select(r => r.Content).ShouldBe([ConversationGroup.UncheckedAnswer, ""]);
    }

    // The guest is nobody an owner can disable, and the others in the household are unaffected.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnotherPersonsOrTheGuestsTurn_RunsBesideADisabledMember(bool guest)
    {
        var channel = guest ? "voice" : ChatChannel;
        var run = new Run(channel, standing: new FakeAccountStanding(_mallory));
        run.Channel.WriteMessage(Message(channel) with
        {
            Assertion = guest ? TestAssertions.OfGuest(Household, channel) : TestAssertions.Of(_fran, channel)
        });

        await run.MonitorAsync();

        run.Errors.ShouldBeEmpty();
        run.Factory.Created.ShouldHaveSingleItem();
    }

    // The operator suspended the household: whoever sent the turn is told so where they wait, the way a
    // failed turn ends, the errors feed says whose, and nothing is built or asked of any agent.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATurnOfASuspendedHousehold_IsRefusedReportedAndAnswered(bool guest)
    {
        var run = new Run("voice", households: MonitorTestMocks.Households(suspended: Household));
        run.Channel.WriteMessage(Message("voice") with
        {
            Principal = guest ? new Principal("voice", "household") : new Principal("voice", _fran.UserId, _fran),
            Assertion = guest ? TestAssertions.OfGuest(Household, "voice") : TestAssertions.Of(_fran, "voice"),
            TurnKey = "turn-7"
        });

        await run.MonitorAsync();

        run.Factory.Created.ShouldBeEmpty();
        run.Agent.ReceivedMessages.ShouldBeEmpty();
        var refused = run.Errors.ShouldHaveSingleItem();
        refused.ErrorType.ShouldBe(ChatMonitor.HouseholdSuspendedErrorType);
        refused.TenantId.ShouldBe(Household);
        run.Channel.SentReplies.Select(r => (r.ContentType, r.Content))
            .ShouldBe([(ReplyContentType.Error, ConversationGroup.SuspendedAnswer), (ReplyContentType.StreamComplete, "")]);
        run.Channel.SentReplies.ShouldAllBe(r => r.TurnKey == "turn-7" && r.ConversationId == "conv-1");
    }

    // A conversation's agent is built once, on its first turn: a household suspended while it runs has
    // its next message refused all the same, since every message is asked about as it arrives.
    [Fact]
    public async Task AConversationAlreadyRunning_IsRefusedFromTheMessageAfterItsHouseholdIsSuspended()
    {
        var run = new Run(households: MonitorTestMocks.Households(suspended: Household, from: 2));
        run.Channel.WriteMessage(Message(content: "first") with { Assertion = TestAssertions.Of(_fran, ChatChannel) });
        run.Channel.WriteMessage(Message(content: "second") with { Assertion = TestAssertions.Of(_fran, ChatChannel) });

        await run.MonitorAsync();

        run.Factory.Created.ShouldHaveSingleItem();
        run.Agent.ReceivedMessages.ShouldHaveSingleItem().ShouldHaveSingleItem().Text.ShouldBe("first");
        run.Errors.ShouldHaveSingleItem().ErrorType.ShouldBe(ChatMonitor.HouseholdSuspendedErrorType);
        run.Channel.SentReplies.ShouldContain(r => r.Content == ConversationGroup.SuspendedAnswer);
    }

    // The Sugary app is a feature of the household's: turned off, the app's estimates are refused and
    // said so, rather than run on an estimator its features took its own server — and its rules — from.
    // The household's other channels answer as before.
    [Fact]
    public async Task ASugaryTurn_OfAHouseholdThatTurnedSugaryOff_IsRefusedAndAnswered_AndAChatTurnIsNot()
    {
        var off = new FeaturesSection { Sugary = false };
        var sugary = new Run(ChannelIds.Sugary,
            households: MonitorTestMocks.Households(suspended: "0123456789abcdef0123456789abcdef", features: off));
        sugary.Channel.WriteMessage(Message(ChannelIds.Sugary) with
        {
            Principal = new Principal(ChannelIds.Sugary, _fran.UserId, _fran),
            Assertion = TestAssertions.Of(_fran, ChannelIds.Sugary),
            AgentId = "sugary",
            TurnKey = "meal-1"
        });
        var chat = new Run(households: MonitorTestMocks.Households(suspended: "0123456789abcdef0123456789abcdef", features: off));
        chat.Channel.WriteMessage(Message() with { Assertion = TestAssertions.Of(_fran, ChatChannel) });

        await sugary.MonitorAsync();
        await chat.MonitorAsync();

        sugary.Factory.Created.ShouldBeEmpty();
        sugary.Errors.ShouldHaveSingleItem().ErrorType.ShouldBe(ChatMonitor.FeatureOffErrorType);
        sugary.Channel.SentReplies.Select(r => (r.ContentType, r.Content))
            .ShouldBe([(ReplyContentType.Error, RefusalAnswers.SugaryOff), (ReplyContentType.StreamComplete, "")]);
        chat.Errors.ShouldBeEmpty();
        chat.Agent.ReceivedMessages.ShouldHaveSingleItem();
    }

    // Another household's suspension is nothing to this one's turns.
    [Fact]
    public async Task ATurnOfAHouseholdThatRuns_IsAnswered_WhileAnotherIsSuspended()
    {
        var run = new Run(households: MonitorTestMocks.Households(suspended: "0123456789abcdef0123456789abcdef"));
        run.Channel.WriteMessage(Message() with { Assertion = TestAssertions.Of(_fran, ChatChannel) });

        await run.MonitorAsync();

        run.Errors.ShouldBeEmpty();
        run.Agent.ReceivedMessages.ShouldHaveSingleItem();
    }

    // The guest is somebody too, but only a satellite hears one: a guest's assertion on the channel it
    // was signed for and no other.
    [Fact]
    public async Task AGuestsAssertionForAnotherChannel_IsRefused()
    {
        var run = new Run();
        run.Channel.WriteUnsigned(Message() with { Assertion = TestAssertions.OfGuest(Household, "voice") });

        await run.MonitorAsync();

        run.Factory.Created.ShouldBeEmpty();
        run.Errors.ShouldHaveSingleItem().Message.ShouldNotBeNull().ShouldContain(TurnAdmission.WrongChannel);
    }

    // Checked as the turn arrives, not when it gets its turn: a message queued behind a long one —
    // the chat client sends while a reply is still streaming — is not refused for the minutes it
    // waited, and once admitted it acts for its person whatever the clock says.
    [Fact]
    public async Task ATurnQueuedPastItsAssertionsExpiry_StillActsForItsPerson()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var keys = new CountingKeys(TestAssertions.Keys);
        var lifetime = TimeSpan.FromMinutes(10);
        var run = new Run(
            verifier: new AssertionVerifier(keys, clock),
            turnGate: async _ =>
            {
                // The first turn holds until the second has been checked, then runs past its expiry.
                await Eventually.Until(() => keys.Lookups >= 2, "the queued turn to be checked as it arrived");
                clock.Advance(TimeSpan.FromMinutes(15));
            });
        run.Channel.WriteMessage(Message(content: "first") with
        {
            Assertion = TestAssertions.Of(_fran, ChatChannel, now: clock.GetUtcNow(), lifetime: lifetime)
        });
        run.Channel.WriteMessage(Message(content: "second") with
        {
            Assertion = TestAssertions.Of(_fran, ChatChannel, now: clock.GetUtcNow(), lifetime: lifetime)
        });

        await run.MonitorAsync();

        run.Errors.ShouldBeEmpty();
        var turns = run.Agent.ReceivedMessages.ToList();
        turns.Count.ShouldBe(2);
        turns[1].ShouldHaveSingleItem().GetConversationContext().ShouldNotBeNull()
            .Identity.ShouldNotBeNull().Subject.ShouldBe(_fran.UserId);
    }

    // Nobody's assertion stands behind a fire: it is never refused for lacking one, and acts for
    // whoever its delegation verifies as, or nobody (ChatMonitorDelegationTests).
    [Fact]
    public async Task AFire_NeedsNoAssertion()
    {
        var run = new Run("scheduling");
        run.Channel.WriteUnsigned(Message("scheduling") with
        {
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "s-1"),
            Principal = new Principal(ChatChannel, _fran.UserId, _fran)
        });

        await run.MonitorAsync();

        run.Errors.ShouldBeEmpty();
        run.TurnContext().Identity.ShouldBeNull();
    }

    // A command steers the conversation and acts for nobody, so it is no turn to vouch for.
    [Fact]
    public async Task AChatCommand_NeedsNoAssertion()
    {
        var run = new Run();
        run.Channel.WriteUnsigned(Message(content: "/clear"));

        await run.MonitorAsync();

        run.Errors.ShouldBeEmpty();
        run.Channel.SentReplies.ShouldBeEmpty();
    }

    private static ChannelMessage Message(string channel = ChatChannel, string content = "hola") => new()
    {
        ConversationId = "conv-1",
        Content = content,
        Sender = "Fran",
        ChannelId = channel,
        AgentId = "jonas"
    };

    private sealed class Run
    {
        private readonly AssertionVerifier _verifier;
        private readonly IAccountStanding? _standing;
        private readonly IAgentDefinitionProvider? _households;
        private readonly List<MetricEvent> _published = [];

        public Run(
            string channel = ChatChannel, AssertionVerifier? verifier = null, Func<CancellationToken, Task>? turnGate = null,
            AgentResponseUpdate[]? replies = null, IAccountStanding? standing = null,
            IAgentDefinitionProvider? households = null)
        {
            _verifier = verifier ?? MonitorTestMocks.Assertions;
            _standing = standing;
            _households = households;
            Channel = new FakeChannelConnection { ChannelId = channel };
            Agent = new FakeAiAgent { TurnGate = turnGate, UpdatesToYield = replies ?? [] };
            Factory = MonitorTestMocks.CreateAgentFactory(Agent);
        }

        public FakeChannelConnection Channel { get; }

        public FakeAiAgent Agent { get; }

        public FakeAgentFactory Factory { get; }

        public IReadOnlyList<ErrorEvent> Errors => [.. Published.OfType<ErrorEvent>()];

        public IReadOnlyList<MetricEvent> Published
        {
            get
            {
                lock (_published)
                {
                    return [.. _published];
                }
            }
        }

        public async Task MonitorAsync()
        {
            Channel.Complete();
            var metrics = new Moq.Mock<IMetricsPublisher>();
            metrics.Setup(m => m.Publish(Moq.It.IsAny<MetricEvent>()))
                .Callback<MetricEvent>(e =>
                {
                    lock (_published)
                    {
                        _published.Add(e);
                    }
                });
            var monitor = new ChatMonitor(
                [Channel],
                Factory,
                MonitorTestMocks.CreateThreadResolver(),
                metrics.Object,
                null,
                NullLogger<ChatMonitor>.Instance,
                _verifier,
                standing: _standing,
                agentDefinitions: _households);

            await monitor.Monitor(CancellationToken.None);
        }

        public ConversationContext TurnContext()
        {
            Agent.ReceivedMessages.TryPeek(out var messages).ShouldBeTrue();
            return messages!.ShouldHaveSingleItem().GetConversationContext().ShouldNotBeNull();
        }
    }

    // The accounts test key, counting how often a verifier looks a key up: once per assertion it checks.
    private sealed class CountingKeys(IVerificationKeys inner) : IVerificationKeys
    {
        private int _lookups;

        public int Lookups => Volatile.Read(ref _lookups);

        public ValueTask<VerificationKey?> FindAsync(string keyId, CancellationToken ct)
        {
            Interlocked.Increment(ref _lookups);
            return inner.FindAsync(keyId, ct);
        }
    }
}