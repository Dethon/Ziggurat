using Domain.Tools.Web;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

// One shape rule — letter, dash, number — across both namespaces, as every document claims.
public class ElementRefTests
{
    [Fact]
    public void For_SpellsTheRefLetterDashNumber()
    {
        ElementRef.For(1).ShouldBe("e-1");
        ElementRef.For(42).ShouldBe("e-42");
    }

    [Theory]
    [InlineData("e-1", true)]
    [InlineData("e-127", true)]
    [InlineData("e1", false)]
    [InlineData("e-", false)]
    [InlineData("i-3", false)]
    [InlineData("e-3x", false)]
    [InlineData(null, false)]
    public void IsElementRef_AcceptsOnlyTheDashedShape(string? candidate, bool expected)
    {
        ElementRef.IsElementRef(candidate).ShouldBe(expected);
    }
}