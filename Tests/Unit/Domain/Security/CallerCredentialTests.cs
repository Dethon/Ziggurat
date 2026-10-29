using Accounts.Internal;
using Domain.Security;
using Shouldly;

namespace Tests.Unit.Domain.Security;

// The accounts service references none of the projects that spell SharedSecret, so it spells the
// comparison again for its callers; this holds the copy to the original's answer on every input the
// original's own tests pin, so the two cannot differ once.
public class CallerCredentialTests
{
    [Theory]
    [InlineData("Bearer s3cret", "s3cret")]
    [InlineData(null, "s3cret")]
    [InlineData("", "s3cret")]
    [InlineData("s3cret", "s3cret")]
    [InlineData("Bearer wrong", "s3cret")]
    [InlineData("Bearer s3cre", "s3cret")]
    [InlineData("Bearer S3CRET", "s3cret")]
    [InlineData("Basic s3cret", "s3cret")]
    [InlineData("bearer s3cret", "s3cret")]
    [InlineData("Bearer  s3cret", "s3cret")]
    [InlineData("Bearer anything", "")]
    [InlineData("Bearer ", "")]
    [InlineData(null, "")]
    public void TheAccountsServicesComparison_AnswersAsSharedSecretDoes(string? presented, string configured)
    {
        CallerCredential.Matches(presented, configured).ShouldBe(SharedSecret.Matches(presented, configured));
    }
}