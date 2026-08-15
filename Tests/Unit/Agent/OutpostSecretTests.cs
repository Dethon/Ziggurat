using global::Agent.App;
using Shouldly;

namespace Tests.Unit.Agent;

// The one gate on registration. Anyone who can reach the agent's port can otherwise attach a
// machine to somebody else's assistant, so this is what the secret is for — and an unset secret
// has to mean "nobody", not "everybody".
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