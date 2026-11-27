using Domain.Apps;
using Shouldly;

namespace Tests.Unit.Domain.Apps;

// A built app's id is the part of its hostname that never changes: six lowercase letters and digits,
// drawn at random so one app's id says nothing about another's.
public class BuiltAppIdTests
{
    [Fact]
    public void AMintedId_IsSixLowercaseLettersAndDigits()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => BuiltAppId.Mint()).ToList();

        ids.ShouldAllBe(id => id.Length == 6 && id.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c)));
        ids.ShouldAllBe(id => BuiltAppId.IsValid(id));
        ids.Distinct().Count().ShouldBeGreaterThan(190);
    }

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("abcde")]
    [InlineData("abcdefg")]
    [InlineData("abc-ef")]
    [InlineData("../abc")]
    public void AnythingElse_IsNoId(string id) => BuiltAppId.IsValid(id).ShouldBeFalse();

    // The published app's label, then the apps domain: no household name or tenant id in it.
    [Fact]
    public void TheLink_IsTheSlugAndIdUnderTheAppsDomain() =>
        BuiltAppId.Link("glucose-today", "k3x9p2", "apps.example").ShouldBe("https://glucose-today-k3x9p2.apps.example/");
}