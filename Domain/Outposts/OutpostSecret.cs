using System.Security.Cryptography;
using System.Text;

namespace Domain.Outposts;

// The one gate on an outpost, presented in both directions: the machine when it registers and
// keeps alive, and the agent when it dials the machine back. Two gates, one rule — anyone who can
// reach either port could otherwise attach a machine to somebody else's assistant, or use
// somebody's machine through an assistant that never invited it.
//
// It lives here rather than in either end because both ends have to agree, and a comparison
// written twice is a comparison that can differ once.
public static class OutpostSecret
{
    public const string Scheme = "Bearer ";

    public static string Header(string secret) => Scheme + secret;

    // An unset secret refuses everything. The alternative reading — no secret configured meaning no
    // gate — turns a forgotten environment variable into an open door onto whatever filesystems
    // happen to be on the network.
    public static bool Matches(string? presented, string configured)
    {
        if (string.IsNullOrEmpty(configured)
            || presented is null
            || !presented.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented[Scheme.Length..]),
            Encoding.UTF8.GetBytes(configured));
    }
}