using McpServerSugary.Clips;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// Which moments of a clip become its stills: spread over the clip, the sharpest of each stretch,
// and none from a stretch where nothing is sharp.
public class StillChoiceTests
{
    // Four samples a second over eight seconds, every one equally sharp but the one picked.
    private static List<FrameSample> Even(double blur = 5) =>
        [.. Enumerable.Range(0, 32).Select(i => new FrameSample(i * 0.25, blur))];

    [Fact]
    public void AClipSharpThroughout_GivesOneStillPerStretch_SpreadOverIt()
    {
        var samples = Even();
        samples[2] = samples[2] with { Blur = 4 };
        samples[29] = samples[29] with { Blur = 4.5 };

        var picked = StillChoice.Pick(samples, 8);

        picked.Count.ShouldBe(8);
        picked[0].ShouldBe(0.5);
        picked[^1].ShouldBe(7.25);
        // One from each second of the clip: evenly spread, never two from one stretch.
        picked.Select(at => (int)at).ShouldBe([0, 1, 2, 3, 4, 5, 6, 7]);
    }

    [Fact]
    public void AStretchWithNothingSharp_GivesNoStill()
    {
        var samples = Even().Select(s => s.At is >= 2 and < 5 ? s with { Blur = 17 } : s).ToList();

        var picked = StillChoice.Pick(samples, 8);

        picked.Count.ShouldBe(5);
        picked.ShouldAllBe(at => at < 2 || at >= 5);
    }

    // A frame with no edges at all is worth no more than a blurred one.
    [Fact]
    public void AFrameWithNoEdges_IsNeverPickedOverASharpOne()
    {
        var samples = Even().Select(s => s.At < 1 ? s with { Blur = double.NaN } : s).ToList();

        StillChoice.Pick(samples, 8).ShouldNotContain(at => at < 1);
    }

    // Sharpness is judged against the clip's own sharpest moment, since a plate filmed in a dim
    // restaurant is softer throughout than one filmed at a window.
    [Fact]
    public void AClipSoftThroughout_StillGivesItsStills()
    {
        StillChoice.Pick(Even(blur: 14), 8).Count.ShouldBe(8);
    }

    [Fact]
    public void AClipWithNothingSharpAnywhere_StillGivesOneStillPerStretch()
    {
        StillChoice.Pick(Even(blur: double.NaN), 8).Count.ShouldBe(8);
    }

    [Fact]
    public void AClipOfFewerFramesThanStills_GivesAStillPerFrame()
    {
        StillChoice.Pick([new FrameSample(0, 5), new FrameSample(0.1, 5), new FrameSample(0.2, 5)], 8)
            .ShouldBe([0, 0.1, 0.2]);
    }

    [Fact]
    public void NoFrames_GiveNoStills()
    {
        StillChoice.Pick([], 8).ShouldBeEmpty();
    }
}