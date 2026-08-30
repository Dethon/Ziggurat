using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The one label ladder, facts-in/label-out. Every rung, the fallbacks, the sanitising and the cap
// live in this single function -- the entry and the note both call it, so these facts are the
// whole of "what is this picture called". The Unicode edges are named here because the ladder
// used to exist twice, in two languages, and the two engines disagreed on exactly these; the C#
// behaviour is the surviving semantics.
public class ImageLabelTests
{
    [Fact]
    public void ADescription_SpeaksBeforeEveryOtherRung()
    {
        Label(alt: "Alt text", caption: "Caption", title: "Title", linkText: "Link", src: "/pic.jpg")
            .ShouldBe("Alt text");
    }

    [Fact]
    public void ACaption_SpeaksWhenTheDescriptionIsSilent()
    {
        Label(caption: "Caption text", title: "Title", linkText: "Link", src: "/pic.jpg")
            .ShouldBe("Caption text");
    }

    [Fact]
    public void ATitle_SpeaksWhenDescriptionAndCaptionAreSilent()
    {
        Label(title: "Title text", linkText: "Link", src: "/pic.jpg").ShouldBe("Title text");
    }

    [Fact]
    public void AnEnclosingLinksWords_SpeakWhenTheAttributesAreSilent()
    {
        Label(linkText: "Link text", src: "/pic.jpg").ShouldBe("Link text");
    }

    [Fact]
    public void AFilename_SpeaksWhenNothingOnThePageDoes()
    {
        Label(src: "/gallery/sunset-over-the-bay.jpg").ShouldBe("sunset-over-the-bay.jpg");
    }

    [Fact]
    public void RenderedDimensions_CloseTheLadder()
    {
        Label(src: null, width: 640, height: 480).ShouldBe("640x480");
    }

    [Fact]
    public void AWhitespaceOnlyRung_FallsThroughRatherThanBlankingTheLabel()
    {
        // The historical sync failure: the in-page copy used ||, so a whitespace-only alt
        // short-circuited every later rung to an empty label. Fall back on blank, not on falsy.
        Label(alt: "   ", caption: "Caption text", src: "/pic.jpg").ShouldBe("Caption text");
    }

    [Fact]
    public void ADataUri_CarriesNoFilename()
    {
        Label(src: "data:image/png;base64,iVBORw0KGgo=", width: 640, height: 480).ShouldBe("640x480");
    }

    [Fact]
    public void AFilenameIsReadOffThePathAlone_QueryAndFragmentStripped()
    {
        Label(src: "https://cdn.example.com/a/photo.jpg?token=abc#frag").ShouldBe("photo.jpg");
    }

    [Fact]
    public void AnAddressWithNoFilenameShapedTail_FallsToDimensions()
    {
        Label(src: "/gallery/", width: 300, height: 200).ShouldBe("300x200");
    }

    [Fact]
    public void BracketsAreRoundedAndWhitespaceFlattened()
    {
        Label(alt: "A [bracketed]\ncaption").ShouldBe("A (bracketed) caption");
    }

    [Fact]
    public void AThoroughLabel_ArrivesWhole()
    {
        Label(alt: new string('x', 400)).ShouldBe(new string('x', 400));
    }

    [Fact]
    public void APathologicalLabel_IsCutWithAnEllipsis()
    {
        Label(alt: new string('x', 700)).ShouldBe(new string('x', 500) + "…");
    }

    [Fact]
    public void ALabelWhoseEmojiStraddlesTheCap_IsCutBeforeItNotThroughIt()
    {
        // The cut lands between characters, never inside one: both halves of the old ladder
        // sliced by UTF-16 units, so an astral character on the boundary left as a lone high
        // surrogate -- mojibake in the entry, and different final bytes per side.
        var label = Label(alt: new string('x', 499) + "😀" + new string('y', 300));

        label.ShouldBe(new string('x', 499) + "…");
        label.Any(char.IsSurrogate).ShouldBeFalse();
    }

    [Fact]
    public void AFamilyEmojiStraddlingTheCap_IsDroppedWholeNotDismembered()
    {
        // A ZWJ sequence is one drawn character; cutting inside it leaves whichever family
        // members fit. The cut backs off to the text-element boundary.
        var family = "👨‍👩‍👧";
        var label = Label(alt: new string('x', 498) + family + new string('y', 300));

        label.ShouldBe(new string('x', 498) + "…");
    }

    [Fact]
    public void AnEmojiEndingExactlyAtTheCap_IsKept()
    {
        var label = Label(alt: new string('x', 498) + "😀" + new string('y', 300));

        label.ShouldBe(new string('x', 498) + "😀…");
    }

    [Fact]
    public void TheCutStillTrimsTrailingWhitespaceBeforeTheEllipsis()
    {
        // The flattened space sits at position 500 and the emoji straddles the cap: the cut keeps
        // the space, the trim drops it, and the ellipsis follows the last visible character.
        var label = Label(alt: new string('x', 499) + " 😀" + new string('y', 300));

        label.ShouldBe(new string('x', 499) + "…");
    }

    [Fact]
    public void ANextLineCharacter_IsFlattenedLikeAnyOtherWhitespace()
    {
        // U+0085 is whitespace to .NET and not to JavaScript's \s -- the surviving semantics
        // flatten it, so an exotic space can never make the entry and the note disagree.
        Label(alt: "before\u0085after").ShouldBe("before after");
    }

    [Fact]
    public void ANextLineOnlyRung_IsBlankAndFallsThrough()
    {
        Label(alt: "\u0085", caption: "Caption text", src: "/pic.jpg").ShouldBe("Caption text");
    }

    [Fact]
    public void AZeroWidthNoBreakSpace_IsNotWhitespaceHere()
    {
        // U+FEFF is whitespace to JavaScript's \s and not to .NET. The surviving semantics keep
        // it: a rung carrying only it is spoken, not blank. Named so the choice is a fact rather
        // than an accident.
        Label(alt: "\uFEFF", caption: "Caption text", src: "/pic.jpg").ShouldBe("\uFEFF");
    }

    [Fact]
    public void AFilenameWithATrailingNewline_IsJudgedTrimmed()
    {
        // The two engines anchored the filename gate differently against a trailing newline
        // (.NET's $ matches before one, JavaScript's does not); the surviving gate never sees
        // one, because the candidate is trimmed first.
        Label(src: "/gallery/pic.jpg\n").ShouldBe("pic.jpg");
    }

    [Fact]
    public void AFilenameWithAnInteriorNewline_FailsTheGate()
    {
        Label(src: "/gallery/pic.jpg\nextra", width: 300, height: 200).ShouldBe("300x200");
    }

    private static string Label(
        string? alt = null,
        string? caption = null,
        string? title = null,
        string? linkText = null,
        string? src = null,
        int width = 0,
        int height = 0) =>
        ImageLabel.From(new ImageLabelFacts(alt, caption, title, linkText, src, width, height));
}