using Domain.Extensions;
using Shouldly;

namespace Tests.Unit.Domain.Extensions;

// A finished reply's notification shows what the reply says, as a phone shows it: one line of text,
// cut at a word, never the markdown or the braces the model wrote.
public sealed class ReplyExcerptTests
{
    [Fact]
    public void AShortReply_IsItsTextWhole() =>
        "**Two flats** fit your budget.".Excerpt(140).ShouldBe("Two flats fit your budget.");

    [Fact]
    public void AStructuredAnswer_IsItsWords() =>
        """{"dish":"Paella","note":"About 64 g of carbs"}""".Excerpt(140).ShouldBe("Paella · About 64 g of carbs");

    [Fact]
    public void ALongReply_IsCutAtAWord()
    {
        var excerpt = "The first flat is near the station and the second one has a terrace".Excerpt(30);

        excerpt.ShouldBe("The first flat is near the…");
    }

    [Fact]
    public void AReplyOfOneLongWord_IsCutWhereItRunsOut() =>
        new string('a', 40).Excerpt(10).ShouldBe(new string('a', 10) + "…");

    // An emoji is two UTF-16 units; cutting between them leaves a phone drawing a broken glyph.
    [Fact]
    public void ACutNeverSplitsACharacter() =>
        ("abcdefghi" + "\U0001F600" + "xyz").Excerpt(10).ShouldBe("abcdefghi…");
}