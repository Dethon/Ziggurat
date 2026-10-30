using System.Security.Cryptography;
using System.Text;

namespace Accounts.Internal;

// Domain.Security.SharedSecret's comparison, spelled again because this service references none of
// the projects that spell it: a bearer token, compared in constant time, and nothing let through
// when none is configured. A test holds the two to the same answers.
public static class CallerCredential
{
    private const string Scheme = "Bearer ";

    public static bool Matches(string? presented, string configured) =>
        !string.IsNullOrEmpty(configured)
        && presented is not null
        && presented.StartsWith(Scheme, StringComparison.Ordinal)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented[Scheme.Length..]), Encoding.UTF8.GetBytes(configured));
}