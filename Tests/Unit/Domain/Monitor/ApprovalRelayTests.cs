using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// An approval is the asker's: put where the turn asked it, offered on the person's other channels,
// settled by the first answer, and every channel that did not answer told who did.
public class ApprovalRelayTests
{
    private static readonly Account _fran = new("home", "fran");

    private static readonly IReadOnlyList<ToolApprovalRequest> _buy =
        [new ToolApprovalRequest("msg-1", "mcp__shop__buy", new Dictionary<string, object?>())];

    private readonly ArmedClock _clock = new();

    private ApprovalRelay Relay(ApprovalChannel anchor, params ApprovalChannel[] others) =>
        new(anchor, [anchor, .. others], NullLogger.Instance, _clock);

    private static ConversationContext PersonsTurn(string channel, Account? account) =>
        new("jonas", "1:2", account?.UserId ?? "household", new ReplyTarget(channel, "1:2"),
            Principal: new Principal(channel, account?.UserId ?? "household", account));

    private static ApprovalAsk Ask(ConversationContext? turn) => ApprovalAsk.For("1:2", _buy, turn);

    [Fact]
    public async Task TheAskersTelegramAnswer_SettlesAVoiceAskedApproval_AndTheOthersAreToldAnsweredElsewhere()
    {
        var voice = new ApprovalChannel("voice");
        var telegram = new ApprovalChannel("telegram");
        var webchat = new ApprovalChannel("signalr");
        var ask = Ask(PersonsTurn("voice", _fran));

        var answered = Relay(voice, telegram, webchat).RequestApprovalAsync(ask, CancellationToken.None);
        await Eventually.Until(() => telegram.Offered.Count == 1 && webchat.Offered.Count == 1, "the offers to go out");
        telegram.Answer(ToolApprovalResult.Approved);

        (await answered).ShouldBe(ToolApprovalResult.Approved);
        await Eventually.Until(() => voice.Settled.Count == 1 && webchat.Settled.Count == 1, "the others to be settled");
        voice.Settled.ShouldHaveSingleItem().ShouldBe((ask.ApprovalId, ToolApprovalResult.Approved));
        webchat.Settled.ShouldHaveSingleItem().ShouldBe((ask.ApprovalId, ToolApprovalResult.Approved));
        telegram.Settled.ShouldBeEmpty();
        // The prompt still being spoken stops asking: its call is called off.
        await Eventually.Until(() => voice.CalledOff, "the voice prompt to stop asking");
    }

    [Fact]
    public async Task TheOffers_NameTheAskerAndTheQuestion_AndTheAnchorIsAskedIt()
    {
        var webchat = new ApprovalChannel("signalr");
        var telegram = new ApprovalChannel("telegram");
        var ask = Ask(PersonsTurn("signalr", _fran));

        _ = Relay(webchat, telegram).RequestApprovalAsync(ask, CancellationToken.None);

        await Eventually.Until(() => telegram.Offered.Count == 1, "the offer to go out");
        webchat.Asked.ShouldHaveSingleItem().ShouldBe(ask);
        webchat.Offered.ShouldBeEmpty();
        var offered = telegram.Offered.ShouldHaveSingleItem();
        offered.ShouldBe(ask);
        offered.Asker.ShouldBe(_fran);
    }

    // A schedule's fire acts for whoever wrote the schedule, so the question is theirs: offered to
    // its creator's account and nobody else's.
    [Fact]
    public async Task AFiresApproval_IsOfferedToTheSchedulesCreatorOnly()
    {
        var webchat = new ApprovalChannel("signalr");
        var telegram = new ApprovalChannel("telegram");
        var fire = new ConversationContext(
            "jonas", "1:2", "fran", new ReplyTarget("signalr", "1:2"),
            OriginKind: MessageOriginKind.Schedule,
            Principal: new Principal("signalr", "fran", _fran));

        _ = Relay(webchat, telegram).RequestApprovalAsync(Ask(fire), CancellationToken.None);

        await Eventually.Until(() => telegram.Offered.Count == 1, "the offer to go out");
        telegram.Offered.ShouldHaveSingleItem().Asker.ShouldBe(_fran);
    }

    // The voice guest is nobody, and a fire nobody stamped acts for nobody: there is no person to
    // find elsewhere, so the question stays where the turn asked it, under that channel's own rule.
    [Fact]
    public async Task AnApprovalActingForNobody_IsAskedWhereTheTurnAskedItAlone()
    {
        var voice = new ApprovalChannel("voice");
        var telegram = new ApprovalChannel("telegram");

        var answered = Relay(voice, telegram).RequestApprovalAsync(Ask(PersonsTurn("voice", null)), CancellationToken.None);
        await Eventually.Until(() => voice.Asked.Count == 1, "the anchor to be asked");
        voice.Answer(ToolApprovalResult.Rejected);

        (await answered).ShouldBe(ToolApprovalResult.Rejected);
        await Eventually.Settle();
        telegram.Offered.ShouldBeEmpty();
        telegram.Settled.ShouldBeEmpty();
    }

    // Where the turn asked is authoritative about ending the question: its answer, its refusal and
    // its time-out all settle every offer.
    [Fact]
    public async Task TheAnchorsAnswer_SettlesEveryOffer()
    {
        var webchat = new ApprovalChannel("signalr");
        var telegram = new ApprovalChannel("telegram");
        var ask = Ask(PersonsTurn("signalr", _fran));

        var answered = Relay(webchat, telegram).RequestApprovalAsync(ask, CancellationToken.None);
        await Eventually.Until(() => telegram.Offered.Count == 1, "the offer to go out");
        webchat.Answer(ToolApprovalResult.Rejected);

        (await answered).ShouldBe(ToolApprovalResult.Rejected);
        await Eventually.Until(() => telegram.Settled.Count == 1, "the offer to be settled");
        telegram.Settled.ShouldHaveSingleItem().ShouldBe((ask.ApprovalId, ToolApprovalResult.Rejected));
        await Eventually.Until(() => telegram.CalledOff, "the offer to be called off");
    }

    // An offer that is not answered — a channel that cannot reach the person, or one that failed —
    // never ends the question: the person may still answer where they were asked.
    [Fact]
    public async Task AnOfferThatCannotReachThePerson_LeavesTheQuestionOpen()
    {
        var webchat = new ApprovalChannel("signalr");
        var telegram = new ApprovalChannel("telegram");
        var failing = new ApprovalChannel("broken") { OfferFailure = new InvalidOperationException("down") };

        var answered = Relay(webchat, telegram, failing)
            .RequestApprovalAsync(Ask(PersonsTurn("signalr", _fran)), CancellationToken.None);
        await Eventually.Until(() => telegram.Offered.Count == 1, "the offer to go out");
        telegram.Answer(null);

        await Eventually.Settle();
        answered.IsCompleted.ShouldBeFalse();
        webchat.Answer(ToolApprovalResult.Approved);
        (await answered).ShouldBe(ToolApprovalResult.Approved);
    }

    // A question the anchor could not show anyone — no live session, no satellite — is not a
    // refusal while the person can be reached elsewhere; they are given a while to answer there.
    [Fact]
    public async Task AQuestionTheAnchorCouldNotShow_IsLeftToTheOffers()
    {
        var voice = new ApprovalChannel("voice");
        var telegram = new ApprovalChannel("telegram");
        var ask = Ask(PersonsTurn("voice", _fran));

        var answered = Relay(voice, telegram).RequestApprovalAsync(ask, CancellationToken.None);
        await Eventually.Until(() => telegram.Offered.Count == 1, "the offer to go out");
        voice.Answer(null);
        await _clock.WaitUntilArmedAsync(ApprovalRelay.UnshownWindow);
        telegram.Answer(ToolApprovalResult.ApprovedAndRemember);

        (await answered).ShouldBe(ToolApprovalResult.ApprovedAndRemember);
    }

    [Fact]
    public async Task AQuestionNobodyAnswersAnywhere_IsRefusedOnceItsWindowIsOver()
    {
        var voice = new ApprovalChannel("voice");
        var telegram = new ApprovalChannel("telegram");

        var answered = Relay(voice, telegram).RequestApprovalAsync(Ask(PersonsTurn("voice", _fran)), CancellationToken.None);
        await Eventually.Until(() => telegram.Offered.Count == 1, "the offer to go out");
        voice.Answer(null);
        await _clock.AdvancePastAsync(ApprovalRelay.UnshownWindow);

        (await answered).ShouldBe(ToolApprovalResult.Rejected);
        await Eventually.Until(() => telegram.CalledOff, "the offer to be called off");
        telegram.Settled.ShouldBeEmpty();
    }

    [Fact]
    public async Task AQuestionNobodyCouldBeShown_IsRefused()
    {
        var voice = new ApprovalChannel("voice");
        var telegram = new ApprovalChannel("telegram");

        var answered = Relay(voice, telegram).RequestApprovalAsync(Ask(PersonsTurn("voice", _fran)), CancellationToken.None);
        await Eventually.Until(() => telegram.Offered.Count == 1, "the offer to go out");
        voice.Answer(null);
        telegram.Answer(null);

        (await answered).ShouldBe(ToolApprovalResult.Rejected);
    }

    // Stopping the turn stops the question everywhere, the way it always stopped it where it was asked.
    [Fact]
    public async Task StoppingTheTurn_CallsTheQuestionOffEverywhere()
    {
        var webchat = new ApprovalChannel("signalr");
        var telegram = new ApprovalChannel("telegram");
        using var turn = new CancellationTokenSource();

        var answered = Relay(webchat, telegram).RequestApprovalAsync(Ask(PersonsTurn("signalr", _fran)), turn.Token);
        await Eventually.Until(() => telegram.Offered.Count == 1, "the offer to go out");
        await turn.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(answered);
        webchat.CalledOff.ShouldBeTrue();
        telegram.CalledOff.ShouldBeTrue();
    }

    [Fact]
    public async Task AutoApprovals_AreToldWhereTheTurnAskedAlone()
    {
        var webchat = new ApprovalChannel("signalr");
        var telegram = new ApprovalChannel("telegram");

        await Relay(webchat, telegram).NotifyAutoApprovedAsync("1:2", _buy, CancellationToken.None);

        webchat.Notified.ShouldHaveSingleItem().ShouldBe("1:2");
        telegram.Notified.ShouldBeEmpty();
    }
}

// A channel whose answer the test gives, recording what it was asked, offered and told.
internal sealed class ApprovalChannel(string channelId) : IChannelConnection
{
    private readonly TaskCompletionSource<ToolApprovalResult?> _answer =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock _gate = new();
    private readonly List<ApprovalAsk> _asked = [];
    private readonly List<ApprovalAsk> _offered = [];
    private readonly List<(string, ToolApprovalResult)> _settled = [];
    private readonly List<string> _notified = [];
    private volatile bool _calledOff;

    public Exception? OfferFailure { get; init; }

    public string ChannelId => channelId;
    public bool AttachOnly => false;
    public bool HostsReviewCopies => false;
    public IAsyncEnumerable<ChannelMessage> Messages => AsyncEnumerable.Empty<ChannelMessage>();

    public IReadOnlyList<ApprovalAsk> Asked => Snapshot(_asked);
    public IReadOnlyList<ApprovalAsk> Offered => Snapshot(_offered);
    public IReadOnlyList<(string ApprovalId, ToolApprovalResult Result)> Settled => Snapshot(_settled);
    public IReadOnlyList<string> Notified => Snapshot(_notified);
    public bool CalledOff => _calledOff;

    public void Answer(ToolApprovalResult? result) => _answer.TrySetResult(result);

    public Task<ToolApprovalResult?> AskApprovalAsync(ApprovalAsk ask, CancellationToken ct) =>
        WaitAsync(_asked, ask, ct);

    public Task<ToolApprovalResult?> OfferApprovalAsync(ApprovalAsk ask, CancellationToken ct) =>
        OfferFailure is null ? WaitAsync(_offered, ask, ct) : Task.FromException<ToolApprovalResult?>(OfferFailure);

    public Task SettleApprovalAsync(string approvalId, ToolApprovalResult result, CancellationToken ct)
    {
        lock (_gate)
        {
            _settled.Add((approvalId, result));
        }

        return Task.CompletedTask;
    }

    public Task NotifyAutoApprovedAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
    {
        lock (_gate)
        {
            _notified.Add(conversationId);
        }

        return Task.CompletedTask;
    }

    private async Task<ToolApprovalResult?> WaitAsync(List<ApprovalAsk> into, ApprovalAsk ask, CancellationToken ct)
    {
        lock (_gate)
        {
            into.Add(ask);
        }

        await using var _ = ct.Register(() => _calledOff = true);
        return await _answer.Task.WaitAsync(ct);
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }

    public Task SendReplyAsync(SendReplyParams reply, CancellationToken ct) => Task.CompletedTask;

    public Task<byte[]?> FetchAttachmentAsync(string attachmentId, CancellationToken ct) =>
        Task.FromResult<byte[]?>(null);

    public Task<bool> StoreAttachmentAsync(
        string conversationId, AttachmentReference attachment, byte[] bytes, CancellationToken ct) =>
        Task.FromResult(false);

    public Task<string?> CreateConversationAsync(
        string agentId, string topicName, string sender, string? initialPrompt, string? address,
        string? existingConversationId, Principal? principal, bool group, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}