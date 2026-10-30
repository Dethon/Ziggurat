using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Monitor;
using Domain.Security;
using Domain.Security.Signing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A person's turn acts for whoever the accounts service vouched for, never for whoever the channel
// server says: the agent checks the assertion the turn arrived with — signed by the accounts service,
// in date, for the channel it came on, a person or the household's guest — as it arrives, refuses one
// that fails and says so on the errors feed and to the channel, and holds what it verified for the
// whole turn. A fire carries none (its delegation is ticket 07's), and a chat command is no turn.
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

    // Nobody's assertion stands behind a fire: it runs as its server stamped it until delegations
    // (ticket 07) say who it may act for.
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
        private readonly List<MetricEvent> _published = [];

        public Run(string channel = ChatChannel, AssertionVerifier? verifier = null, Func<CancellationToken, Task>? turnGate = null)
        {
            _verifier = verifier ?? MonitorTestMocks.Assertions;
            Channel = new FakeChannelConnection { ChannelId = channel };
            Agent = new FakeAiAgent { TurnGate = turnGate };
            Factory = MonitorTestMocks.CreateAgentFactory(Agent);
        }

        public FakeChannelConnection Channel { get; }

        public FakeAiAgent Agent { get; }

        public FakeAgentFactory Factory { get; }

        public IReadOnlyList<ErrorEvent> Errors
        {
            get
            {
                lock (_published)
                {
                    return [.. _published.OfType<ErrorEvent>()];
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
                _verifier);

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