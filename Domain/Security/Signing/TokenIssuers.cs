namespace Domain.Security.Signing;

// The `iss` each of the deployment's two issuers signs as, spelled once because the issuer and
// every verifier of its tokens must agree on it and neither references the other.
public static class TokenIssuers
{
    // Assertions: who a turn is for, signed where a person is resolved.
    public const string Accounts = "ziggurat-accounts";

    // Call tokens, bound to the one server a call is for, and the delegations deferred turns act on.
    public const string Agent = "ziggurat-agent";
}