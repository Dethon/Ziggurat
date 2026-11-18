using Domain.Contracts;
using McpServerSugary.Glucose;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// The poller and the watch engine ask which households turn Sugary off on every sweep. An answer that
// cannot be had is every household on, said once per outage, and not asked again for a minute, so a
// hung accounts service does not hold every sweep for its timeout.
public class SugaryOffHouseholdsTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero));
    private readonly Mock<IHouseholdConfiguration> _configuration = new();
    private readonly Mock<ILogger<SugaryOffHouseholds>> _logger = new();
    private int _asked;
    private Exception? _failure = new HttpRequestException("accounts is away");

    public SugaryOffHouseholdsTests() =>
        _configuration.Setup(c => c.HouseholdsWithSugaryOffAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                _asked++;
                return _failure is { } failure
                    ? Task.FromException<IReadOnlyList<string>>(failure)
                    : Task.FromResult<IReadOnlyList<string>>(["household-off"]);
            });

    private SugaryOffHouseholds Households() => new(_configuration.Object, _clock, _logger.Object);

    [Fact]
    public async Task AnAnswerThatCannotBeHad_IsEveryHouseholdOn_AskedAgainOnlyAfterAMinute()
    {
        var households = Households();

        (await households.OffAsync(CancellationToken.None)).ShouldBeEmpty();
        _clock.Advance(SugaryOffHouseholds.AfterFailure - TimeSpan.FromSeconds(1));
        (await households.IsOffAsync("household-off", CancellationToken.None)).ShouldBeFalse();
        _asked.ShouldBe(1);

        _failure = null;
        _clock.Advance(TimeSpan.FromSeconds(1));

        (await households.IsOffAsync("household-off", CancellationToken.None)).ShouldBeTrue();
        _asked.ShouldBe(2);
    }

    [Fact]
    public async Task AnOutage_IsSaidOnce_AndItsEndOnce()
    {
        var households = Households();

        await households.OffAsync(CancellationToken.None);
        _clock.Advance(SugaryOffHouseholds.AfterFailure);
        await households.OffAsync(CancellationToken.None);
        _failure = null;
        _clock.Advance(SugaryOffHouseholds.AfterFailure);
        await households.OffAsync(CancellationToken.None);
        await households.OffAsync(CancellationToken.None);

        _asked.ShouldBe(4);
        Logged(LogLevel.Warning).ShouldBe(1);
        Logged(LogLevel.Information).ShouldBe(1);
    }

    // Its own cancellation is the caller's to see, never an outage.
    [Fact]
    public async Task ACancelledAsk_IsACancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _failure = new OperationCanceledException(cancelled.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => Households().OffAsync(cancelled.Token));
        Logged(LogLevel.Warning).ShouldBe(0);
    }

    private int Logged(LogLevel level) =>
        _logger.Invocations.Count(i => i.Method.Name == nameof(ILogger.Log) && i.Arguments[0] is LogLevel logged && logged == level);
}