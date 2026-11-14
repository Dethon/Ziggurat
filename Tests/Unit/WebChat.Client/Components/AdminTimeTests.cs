using Shouldly;
using Tests.E2E.Fixtures;
using WebChat.Client.Components.Admin;

namespace Tests.Unit.WebChat.Client.Components;

// When something happened, written the one way the operator's dashboard writes it — day first, a
// 24-hour clock, in the reader's own zone — rather than by whatever culture the browser reports, which
// put "11/1/2026 11:03 PM" on an owner's page beside the dashboard's "01/11/2026 23:03".
public sealed class AdminTimeTests
{
    [Fact]
    public void AnInstant_IsWrittenDayFirstOnATwentyFourHourClockInTheReadersZone()
    {
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

        AdminTime.Of(new DateTimeOffset(2026, 11, 1, 22, 3, 0, TimeSpan.Zero), madrid).ShouldBe("01/11/2026 23:03");
    }

    [Fact]
    public void NoAdminPage_FormatsATimeItself()
    {
        var root = Path.Combine(TestHelpers.FindSolutionRoot(), "WebChat.Client");
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "Pages", "Admin"), "*.razor")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Components", "Admin"), "*.razor"))
            .Where(file => File.ReadAllText(file).Contains("ToLocalTime()"))
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty("an admin page writes a time with AdminTime.Of");
    }
}