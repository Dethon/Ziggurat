using Dashboard.Client.State.Tokens;
using Domain.DTOs.Metrics;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

// Whom a listed event's account stands for. A memory call and Jev's judgments name only the account
// as their sender, while a turn names the person, so the operator's activity feed and tables showed
// a person as a 32-character id beside their own name. A list shows the latest name the account's
// calls carried, as the breakdowns do; an account no call named stays its id.
public sealed class PersonNamesTests
{
    private static readonly DateTimeOffset _morning = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static TokenUsageEvent Call(string? userId, string sender, DateTimeOffset at) => new()
    {
        Sender = sender, UserId = userId, Model = "m", InputTokens = 1, OutputTokens = 0, Cost = 0m, Timestamp = at
    };

    [Fact]
    public void AnAccount_IsShownUnderTheLatestNameItsCallsCarried_NeverItsId()
    {
        var names = PersonNames.From(
        [
            Call("acc-ana", "Ana", _morning),
            Call("acc-ana", "Ana María", _morning.AddHours(1)),
            Call("acc-ana", "acc-ana", _morning.AddHours(2))
        ]);

        names.Of("acc-ana", fallback: "acc-ana").ShouldBe("Ana María");
    }

    [Fact]
    public void AnAccountNoCallNamed_AndACallForNoAccount_KeepWhatTheyCarry()
    {
        var names = PersonNames.From([Call("acc-ana", "acc-ana", _morning), Call(null, "guest", _morning)]);

        names.Of("acc-ana", fallback: "acc-ana").ShouldBe("acc-ana");
        names.Of(null, fallback: "guest").ShouldBe("guest");
    }
}