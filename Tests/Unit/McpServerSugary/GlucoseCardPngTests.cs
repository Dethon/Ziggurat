using Domain.DTOs.Sugary;
using McpServerSugary.Glucose;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// The widget's one picture: KWGT refreshes an image it will not refresh a text for, so what a person acts
// on has to be in it, and a picture that stopped updating has to say when it was true.
public class GlucoseCardPngTests
{
    private const int Width = 800;
    private const int Height = 320;

    private static readonly DateTimeOffset _to = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _from = _to.AddHours(-1);
    private static readonly GlucosePalette _palette = GlucosePalette.Dark;

    private static GlucoseCard Card(
        string value = "142", double? mgPerDl = 142, GlucoseTrend trend = GlucoseTrend.Steady, string time = "22:53",
        string unit = "mg/dL") => new(value, unit, trend, time, mgPerDl);

    private static byte[] Png(GlucoseCard card, int width = Width, int height = Height) =>
        GlucoseCardPng.Draw(card, [], _from, _to, 70, 180, width, height, _palette);

    private static PngPixels Draw(GlucoseCard card) => PngPixels.Read(Png(card));

    [Fact]
    public void ALow_IsWrittenInTheDangerColour()
    {
        var pixels = Draw(Card(value: "60", mgPerDl: 60));

        pixels.AnyIn(0, 0, Width / 3, Height / 3, _palette.Danger).ShouldBeTrue();
        pixels.AnyIn(0, 0, Width / 3, Height / 3, _palette.Good).ShouldBeFalse();
    }

    [Fact]
    public void AReadingInRange_IsWrittenInTheGoodColour()
    {
        Draw(Card()).AnyIn(0, 0, Width / 3, Height / 3, _palette.Good).ShouldBeTrue();
    }

    [Theory]
    [InlineData("142", "143")]
    [InlineData("6.7", "6.1")]
    public void ADifferentReading_DrawsADifferentPicture(string one, string other)
    {
        Png(Card(value: one)).ShouldNotBe(Png(Card(value: other)));
    }

    [Fact]
    public void EachTrend_DrawsItsOwnArrow()
    {
        GlucoseTrend[] trends =
        [
            GlucoseTrend.RisingFast, GlucoseTrend.Rising, GlucoseTrend.Steady, GlucoseTrend.Falling,
            GlucoseTrend.FallingFast, GlucoseTrend.Unknown
        ];

        trends.Select(t => Convert.ToHexString(Png(Card(trend: t)))).Distinct().Count().ShouldBe(trends.Length);
    }

    [Fact]
    public void TheReadingsTime_IsInThePicture()
    {
        Png(Card(time: "22:53")).ShouldNotBe(Png(Card(time: "21:15")));
    }

    [Fact]
    public void EveryDigit_IsDrawnDifferently()
    {
        var digits = "0123456789".Select(d => Convert.ToHexString(Png(Card(value: d.ToString())))).Distinct();

        digits.Count().ShouldBe(10);
    }

    [Theory]
    [InlineData("mg/dL")]
    [InlineData("mmol/L")]
    public void BothUnits_AreDrawn(string unit)
    {
        Should.NotThrow(() => Png(Card(unit: unit)));
    }

    [Fact]
    public void NoReadingYet_StillDrawsACard()
    {
        var pixels = PngPixels.Read(Png(Card(value: "--", mgPerDl: null, trend: GlucoseTrend.Unknown, time: "--:--")));

        pixels.Alpha(Width / 2, Height / 2).ShouldBeGreaterThan((byte)128);
    }

    [Fact]
    public void ItsCorners_AreRounded()
    {
        var pixels = Draw(Card());

        pixels.Alpha(0, 0).ShouldBe((byte)0);
        pixels.Alpha(Width / 2, 1).ShouldBeGreaterThan((byte)128);
    }

    [Theory]
    [InlineData(120, 60)]
    [InlineData(1200, 800)]
    public void EverySizeOffered_IsDrawnToTheEdgeWithoutFailing(int width, int height)
    {
        var pixels = PngPixels.Read(Png(Card(value: "18.9", unit: "mmol/L", time: "23:59"), width, height));

        pixels.Width.ShouldBe(width);
        pixels.Height.ShouldBe(height);
    }
}