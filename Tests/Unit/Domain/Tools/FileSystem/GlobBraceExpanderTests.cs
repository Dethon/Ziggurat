using Domain.Tools.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class GlobBraceExpanderTests
{
    [Fact]
    public void Expand_SingleGroup_ExpandsEachAlternativeInOrder()
    {
        GlobBraceExpander.Expand("**/*.{jpg,jpeg,png,gif,bmp,tiff,webp}").ShouldBe([
            "**/*.jpg", "**/*.jpeg", "**/*.png", "**/*.gif", "**/*.bmp", "**/*.tiff", "**/*.webp"
        ]);
    }

    [Fact]
    public void Expand_MultipleGroups_ProducesCartesianProduct()
    {
        GlobBraceExpander.Expand("{a,b}/{c,d}").ShouldBe(["a/c", "a/d", "b/c", "b/d"]);
    }

    [Fact]
    public void Expand_NestedGroups_FlattensToAllLeaves()
    {
        GlobBraceExpander.Expand("img.{a,{b,c}}").ShouldBe(["img.a", "img.b", "img.c"]);
    }

    [Fact]
    public void Expand_EmptyAlternative_YieldsEmptyString()
    {
        GlobBraceExpander.Expand("file{,.bak}").ShouldBe(["file", "file.bak"]);
    }

    [Theory]
    [InlineData("file{1}.txt")]  // no comma → not a group
    [InlineData("a{b,c")]        // unmatched opening brace
    public void Expand_NoBraceExpansion_ReturnsSingleLiteralPattern(string pattern)
    {
        GlobBraceExpander.Expand(pattern).ShouldBe([pattern]);
    }

    // 2^9 = 512, exactly the cap: the largest cartesian product a pattern may still expand to.
    [Fact]
    public void Expand_AtTheExpansionCap_ReturnsEveryPattern()
    {
        var pattern = string.Concat(Enumerable.Repeat("{a,b}", 9));

        GlobBraceExpander.Expand(pattern).Count.ShouldBe(GlobBraceExpander.MaxPatterns);
    }

    // The product is caller-controlled: a few more groups would be 2^30 patterns and an OOM, so
    // crossing the cap fails as an invalid pattern instead of materializing the product.
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public void Expand_OverTheExpansionCap_ThrowsInsteadOfMaterializingTheProduct(int groups)
    {
        var pattern = string.Concat(Enumerable.Repeat("{a,b}", groups));

        Should.Throw<ArgumentException>(() => GlobBraceExpander.Expand(pattern))
            .Message.ShouldContain(GlobBraceExpander.MaxPatterns.ToString());
    }

    [Fact]
    public void Expand_SingleGroupOverTheCap_Throws()
    {
        var body = string.Join(',', Enumerable.Range(0, GlobBraceExpander.MaxPatterns + 1).Select(i => $"a{i}"));

        Should.Throw<ArgumentException>(() => GlobBraceExpander.Expand($"{{{body}}}"));
    }

    // A single level's product is (alternatives × each alternative's expansion × the suffix's), and
    // each of the last two may sit just under the cap: ten alternatives of 2^9 against a suffix of
    // 2^9 is 2.6 million patterns from a pattern of a few hundred characters. The cap has to bite
    // while the combinations are being produced, so crossing it costs a cap-sized list and no more.
    [Fact]
    public void Expand_ProductOfNestedLevels_ThrowsWithoutMaterializingTheProduct()
    {
        var nested = string.Concat(Enumerable.Repeat("{a,b}", 9));
        var pattern = "{" + string.Join(',', Enumerable.Repeat(nested, 10)) + "}" + nested;

        var before = GC.GetAllocatedBytesForCurrentThread();
        Should.Throw<ArgumentException>(() => GlobBraceExpander.Expand(pattern));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBeLessThan(8 * 1024 * 1024);
    }
}