using Domain.Security;
using Shouldly;

namespace Tests.Unit.Domain.Security;

// The one comparison every shared-secret gate uses, pinned here on the outpost's: anyone who can
// reach the agent's port could otherwise attach a machine to somebody else's assistant; anyone who
// can reach the machine's port could otherwise use it through an assistant that never invited it.
// An unset secret has to mean "nobody", not "everybody".
public class SharedSecretTests
{
    [Fact]
    public void TheConfiguredSecretPresentedAsABearerToken_IsAccepted()
    {
        SharedSecret.Matches("Bearer s3cret", "s3cret").ShouldBeTrue();
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
        SharedSecret.Matches(presented, "s3cret").ShouldBeFalse();
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
        SharedSecret.Matches(presented, "").ShouldBeFalse();
    }
}