using System.Globalization;
using Domain.DTOs.Sugary;
using McpServerSugary.Glucose;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

public class LibreLinkUpWireTests
{
    [Theory]
    [InlineData(1, GlucoseTrend.FallingFast)]
    [InlineData(2, GlucoseTrend.Falling)]
    [InlineData(3, GlucoseTrend.Steady)]
    [InlineData(4, GlucoseTrend.Rising)]
    [InlineData(5, GlucoseTrend.RisingFast)]
    [InlineData(0, GlucoseTrend.Unknown)]
    [InlineData(6, GlucoseTrend.Unknown)]
    [InlineData(null, GlucoseTrend.Unknown)]
    public void Trend_MapsTheArrow(int? arrow, GlucoseTrend trend) =>
        LibreLinkUpWire.Trend(arrow).ShouldBe(trend);

    [Theory]
    [InlineData("de-DE")]
    [InlineData("es-ES")]
    [InlineData("ja-JP")]
    public void Instant_ReadsTheFactoryStampAsUtcWhateverTheCulture(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            LibreLinkUpWire.Instant("5/21/2022 1:38:50 PM")
                .ShouldBe(new DateTimeOffset(2022, 5, 21, 13, 38, 50, TimeSpan.Zero));
            LibreLinkUpWire.Instant("11/3/2026 12:04:05 AM")
                .ShouldBe(new DateTimeOffset(2026, 11, 3, 0, 4, 5, TimeSpan.Zero));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2022-05-21T13:38:50Z")]
    [InlineData("21/5/2022 13:38:50")]
    public void Instant_SomethingElse_IsNone(string? stamp) =>
        LibreLinkUpWire.Instant(stamp).ShouldBeNull();

    [Theory]
    [InlineData(null, "https://api.libreview.io/")]
    [InlineData("eu", "https://api-eu.libreview.io/")]
    [InlineData("DE", "https://api-de.libreview.io/")]
    [InlineData("eu2", "https://api-eu2.libreview.io/")]
    [InlineData("ru", "https://api.libreview.ru/")]
    public void Abbott_PlacesEachRegion(string? region, string address) =>
        LibreLinkUpRegions.Abbott.BaseFor(region)!.ToString().ShouldBe(address);

    // A redirect names the region the client goes to next; a region that is not a bare code would
    // send her password wherever the answer said.
    [Theory]
    [InlineData("evil.example/")]
    [InlineData("eu.attacker.com#")]
    [InlineData("a b")]
    public void BaseFor_ARegionThatIsNoCode_IsNone(string region) =>
        LibreLinkUpRegions.Abbott.BaseFor(region).ShouldBeNull();
}