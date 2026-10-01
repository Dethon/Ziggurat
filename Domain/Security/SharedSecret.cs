using System.Security.Cryptography;
using System.Text;

namespace Domain.Security;

// The one comparison behind every shared-secret gate in the repo: an outpost in both directions
// (the machine when it registers and keeps alive, the agent when it dials it back), the agent
// registration API, and every deployment MCP server's /mcp. The secrets differ; the rule does not —
// a bearer token, compared in constant time, and nothing let through when none is configured.
//
// It lives here rather than in any one end because every end has to agree, and a comparison
// written twice is a comparison that can differ once.
public static class SharedSecret
{
    public const string Scheme = "Bearer ";

    public static string Header(string secret) => Scheme + secret;

    // An unset secret refuses everything. The alternative reading — no secret configured meaning no
    // gate — turns a forgotten environment variable into an open door onto whatever is behind it.
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