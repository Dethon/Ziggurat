using System.Text;
using Domain.DTOs.Channel;
using Domain.Security.Sealing;
using McpServerSugary.Glucose;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// A linked account's password, sealed for its owner: it opens for that person under that key, and for
// nobody else and under no other key.
public class GlucoseCredentialsTests
{
    private static readonly Account Her = new("household-home", "user-her");
    private static readonly SealingKey Key = SealingKey.Generate();

    [Fact]
    public void ASealedPassword_OpensForItsOwnerUnderItsKey()
    {
        var credentials = new GlucoseCredentials(Key);

        var sealedPassword = credentials.Seal(Her, "correct horse");

        credentials.Open(Her, sealedPassword).ShouldBe("correct horse");
    }

    [Fact]
    public void ASealedPassword_DoesNotHoldThePasswordInTheClear()
    {
        var sealedPassword = new GlucoseCredentials(Key).Seal(Her, "correct horse");

        Encoding.UTF8.GetString(sealedPassword).ShouldNotContain("correct horse");
    }

    [Theory]
    [InlineData("household-home", "user-him")]
    [InlineData("household-next-door", "user-her")]
    public void ASealedPassword_DoesNotOpenForAnyoneElse(string tenantId, string userId)
    {
        var credentials = new GlucoseCredentials(Key);
        var sealedPassword = credentials.Seal(Her, "correct horse");

        credentials.Open(new Account(tenantId, userId), sealedPassword).ShouldBeNull();
    }

    // The ids are joined so that no two owners make the same associated data, however their ids are
    // spelled: a household "a/b" and a person "c" are not a household "a" and a person "b/c".
    [Fact]
    public void ASealedPassword_DoesNotOpenForIdsThatJoinTheSame()
    {
        var credentials = new GlucoseCredentials(Key);
        var sealedPassword = credentials.Seal(new Account("a/b", "c"), "correct horse");

        credentials.Open(new Account("a", "b/c"), sealedPassword).ShouldBeNull();
    }

    [Fact]
    public void ASealedPassword_DoesNotOpenUnderAnotherKey()
    {
        var sealedPassword = new GlucoseCredentials(Key).Seal(Her, "correct horse");

        new GlucoseCredentials(SealingKey.Generate()).Open(Her, sealedPassword).ShouldBeNull();
    }

    [Fact]
    public void WithoutAKey_NothingIsSealedAndNothingOpens()
    {
        var sealedPassword = new GlucoseCredentials(Key).Seal(Her, "correct horse");
        var none = new GlucoseCredentials(null);

        none.CanSeal.ShouldBeFalse();
        none.Open(Her, sealedPassword).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => none.Seal(Her, "correct horse"));
    }
}