using Domain.Conversations;
using Mcp.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// A channel continues only the conversations it opened, and only for the owner that opened each,
// within a window after its last turn. The owner is whatever the server keys a follow-up by — a
// person, or a person within one app — and the binding compares it and nothing else.
public sealed class OpenConversationsTests
{
    private sealed record Owner(string App, string Person);

    private static readonly TimeSpan _window = TimeSpan.FromHours(2);
    private const int Ceiling = 3;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly OpenConversations<Owner, string> _open;

    public OpenConversationsTests() => _open = new OpenConversations<Owner, string>(_clock, _window, Ceiling);

    [Fact]
    public void TheOwnerThatOpenedAConversation_ContinuesIt_WithWhatItWasOpenedWith()
    {
        var conversation = _open.Open(new Owner("app-1", "her"), "kept");

        _open.TryContinue(new Owner("app-1", "her"), conversation, out var kept).ShouldBeTrue();
        kept.ShouldBe("kept");
    }

    [Fact]
    public void AnotherOwner_DoesNotContinueIt()
    {
        var conversation = _open.Open(new Owner("app-1", "her"), "kept");

        _open.TryContinue(new Owner("app-2", "her"), conversation, out _).ShouldBeFalse();
        _open.TryContinue(new Owner("app-1", "him"), conversation, out _).ShouldBeFalse();
    }

    [Fact]
    public void AConversationItNeverOpened_IsNotContinued() =>
        _open.TryContinue(new Owner("app-1", "her"), "someone-elses", out _).ShouldBeFalse();

    [Fact]
    public void AConversation_ClosesAWindowAfterItsLastTurn_AndEachTurnStartsTheWindowAgain()
    {
        var owner = new Owner("app-1", "her");
        var conversation = _open.Open(owner, "kept");

        _clock.Advance(_window - TimeSpan.FromMinutes(1));
        _open.TryContinue(owner, conversation, out _).ShouldBeTrue();
        _clock.Advance(_window - TimeSpan.FromMinutes(1));
        _open.TryContinue(owner, conversation, out _).ShouldBeTrue();
        _clock.Advance(_window);

        _open.TryContinue(owner, conversation, out _).ShouldBeFalse();
    }

    // The table is bounded: at its ceiling a new conversation forgets the one longest without a turn, which
    // its owner then finds closed and starts afresh, rather than a new one being refused.
    [Fact]
    public void AtItsCeiling_AConversationOpening_ForgetsTheOneLongestWithoutATurn()
    {
        var owner = new Owner("app-1", "her");
        var opened = Enumerable.Range(0, Ceiling).Select(_ =>
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            return _open.Open(owner, "kept");
        }).ToList();
        _open.TryContinue(owner, opened[0], out _).ShouldBeTrue();

        var newest = _open.Open(owner, "kept");

        _open.TryContinue(owner, opened[1], out _).ShouldBeFalse();
        new[] { opened[0], opened[2], newest }.Where(conversation => !_open.TryContinue(owner, conversation, out _))
            .ShouldBeEmpty();
    }

    // Spelled as WebChat spells a conversation, so a review copy can be shown over it (ADR 0046).
    [Fact]
    public void AnOpenedConversation_IsSpelledAsAConversationIdentity() =>
        ConversationIdentity.Parse(_open.Open(new Owner("app-1", "her"), "kept")).ShouldNotBeNull();
}