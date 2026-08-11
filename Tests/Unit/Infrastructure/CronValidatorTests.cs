using Infrastructure.Validation;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class CronValidatorTests
{
    private readonly CronValidator _validator = new();

    [Theory]
    [InlineData("0 9 * * *", true)]       // Every day at 9am
    [InlineData("invalid", false)]
    [InlineData("", false)]
    [InlineData("0 9 * *", false)]        // Missing field
    public void IsValid_VariousExpressions_ReturnsExpected(string cron, bool expected)
    {
        _validator.IsValid(cron).ShouldBe(expected);
    }

    [Fact]
    public void GetNextOccurrence_ValidCron_ReturnsNextTimeInUtc()
    {
        var from = new DateTimeOffset(2024, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var next = _validator.GetNextOccurrence("0 9 * * *", from, TimeZoneInfo.Utc);

        next.ShouldNotBeNull();
        next.Value.Kind.ShouldBe(DateTimeKind.Utc);
        next.Value.ShouldBe(new DateTime(2024, 1, 15, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetNextOccurrence_InvalidCron_ReturnsNull()
    {
        var next = _validator.GetNextOccurrence("invalid", DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        next.ShouldBeNull();
    }

    // 09:00 Madrid is 07:00 UTC in summer (CEST, +02:00) and 08:00 UTC in winter (CET, +01:00):
    // the same cron maps to a different UTC instant across DST, which is the whole point.
    [Theory]
    [InlineData("2026-07-01T00:00:00", "2026-07-01T07:00:00")] // summer (CEST)
    [InlineData("2026-01-10T00:00:00", "2026-01-10T08:00:00")] // winter (CET)
    public void GetNextOccurrence_DailyCron_RespectsZoneDstOffset(string fromUtc, string expectedUtc)
    {
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        var from = new DateTimeOffset(DateTime.Parse(fromUtc), TimeSpan.Zero);

        var next = _validator.GetNextOccurrence("0 9 * * *", from, madrid);

        next.ShouldBe(DateTime.SpecifyKind(DateTime.Parse(expectedUtc), DateTimeKind.Utc));
    }
}