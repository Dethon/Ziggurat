using McpServerSugary.Glucose;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// What the widget's curve must not get wrong: a stretch with no readings never looks measured, and a low
// is drawn as one.
public class GlucoseCurvePngTests
{
    private const int Width = 200;
    private const int Height = 100;

    private static readonly DateTimeOffset _to = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _from = _to.AddHours(-1);

    private static GlucosePoint At(int minutesAfterStart, double mgPerDl) => new(_from.AddMinutes(minutesAfterStart), mgPerDl);

    private static PngPixels Draw(params GlucosePoint[] points) =>
        PngPixels.Read(GlucoseCurvePng.Draw(points, _from, _to, 70, 180, Width, Height, GlucosePalette.Dark));

    [Fact]
    public void AStretchWithNoReadings_IsNotDrawnAcross()
    {
        var pixels = Draw(At(0, 120), At(5, 120), At(10, 120), At(50, 120), At(55, 120), At(60, 120));

        Enumerable.Range(0, Height).ShouldNotContain(y => pixels.Is(Width / 2, y, GlucosePalette.Dark.Good));
        Enumerable.Range(0, Height).ShouldContain(y => pixels.Is(Width / 10, y, GlucosePalette.Dark.Good));
    }

    [Fact]
    public void ALow_IsDrawnInTheDangerColour()
    {
        var pixels = Draw(At(10, 60), At(15, 60), At(20, 60));

        Enumerable.Range(0, Height).ShouldContain(y => pixels.Is(Width / 4, y, GlucosePalette.Dark.Danger));
    }

    [Fact]
    public void ALoneReading_IsStillShown()
    {
        var pixels = Draw(At(30, 120));

        Enumerable.Range(0, Height).ShouldContain(y => pixels.Is(Width / 2, y, GlucosePalette.Dark.Good));
    }

    [Fact]
    public void OutsideHerRange_TheGroundIsTransparent()
    {
        var pixels = Draw(At(30, 120));

        pixels.Alpha(0, 0).ShouldBe((byte)0);
    }
}