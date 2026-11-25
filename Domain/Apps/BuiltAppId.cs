using System.Security.Cryptography;

namespace Domain.Apps;

// A built app's id: six lowercase letters and digits, drawn with a cryptographic generator, unique
// across the deployment (its hostname names no household). The id, never the slug, names its
// repository and ends its hostname, so a renamed app's old links still resolve.
public static class BuiltAppId
{
    public const int Length = 6;
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static string Mint() => RandomNumberGenerator.GetString(Alphabet, Length);

    public static bool IsValid(string? id) => id is { Length: Length } && id.All(Alphabet.Contains);

    // The published app's origin: `<slug>-<id>` under the apps domain.
    public static string Link(string slug, string id, string appsDomain) => $"https://{slug}-{id}.{appsDomain}/";
}