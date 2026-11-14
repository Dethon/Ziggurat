using System.Text.RegularExpressions;
using Dashboard.Client.Components;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Dashboard.Client.Components;

// Every time the dashboard writes is in the reader's own zone. The metric pages wrote an event's
// timestamp as it arrived — UTC, from the publisher's "+00:00" — beside the Households page's local
// times and the owners' admin pages', so the same turn read 04:13 on Tokens and 05:13 everywhere else.
public sealed class DashboardTimeTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
    private static readonly DateTimeOffset _utc = new(2026, 11, 2, 4, 13, 31, TimeSpan.Zero);

    [Fact]
    public void AnEvent_IsWrittenToTheSecondInTheReadersZone() =>
        DashboardTime.Event(_utc, _madrid).ShouldBe("02/11 05:13:31");

    [Fact]
    public void AMoment_IsWrittenToTheMinuteInTheReadersZone() =>
        DashboardTime.Moment(_utc, _madrid).ShouldBe("02/11 05:13");

    [Fact]
    public void ADeadline_IsWrittenWithItsYearInTheReadersZone() =>
        DashboardTime.Deadline(_utc, _madrid).ShouldBe("02/11/2026 05:13");

    [Fact]
    public void NoDashboardPage_FormatsATimeItself()
    {
        var root = Path.Combine(TestHelpers.FindSolutionRoot(), "Dashboard.Client");
        var offenders = Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"ToLocalTime\(\)|\.ToString\(""dd/MM"))
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty("a dashboard page writes a time with DashboardTime");
    }
}