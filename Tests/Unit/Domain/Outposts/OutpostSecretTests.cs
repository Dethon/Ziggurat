using Domain.Outposts;
using Shouldly;

namespace Tests.Unit.Domain.Outposts;

// The one gate, presented in both directions and compared in one place. Anyone who can reach the
// agent's port could otherwise attach a machine to somebody else's assistant; anyone who can reach
// the machine's port could otherwise use it through an assistant that never invited it. An unset
// secret has to mean "nobody", not "everybody".
public class OutpostSecretTests
{
    [Fact]
    public void TheConfiguredSecretPresentedAsABearerToken_IsAccepted()
    {
        OutpostSecret.Matches("Bearer s3cret", "s3cret").ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3cret")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer s3cre")]
    [InlineData("Bearer S3CRET")]
    [InlineData("Basic s3cret")]
    public void AnythingElse_IsRefused(string? presented)
    {
        OutpostSecret.Matches(presented, "s3cret").ShouldBeFalse();
    }

    // A deployment that never set the secret refuses every machine. The alternative — no secret
    // configured meaning no gate — turns a forgotten environment variable into an open door onto
    // whatever filesystems happen to be on the network.
    [Theory]
    [InlineData("Bearer anything")]
    [InlineData("Bearer ")]
    [InlineData(null)]
    public void WithNoSecretConfigured_NothingIsAccepted(string? presented)
    {
        OutpostSecret.Matches(presented, "").ShouldBeFalse();
    }
}