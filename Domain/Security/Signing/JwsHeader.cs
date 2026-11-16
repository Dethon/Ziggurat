using System.Buffers.Text;
using System.Text.Json.Serialization;

namespace Domain.Security.Signing;

internal sealed record JwsHeader(
    [property: JsonPropertyName("alg")] string? Algorithm,
    [property: JsonPropertyName("kid")] string? KeyId,
    [property: JsonPropertyName("typ")] string? Type)
{
    public const string Jwt = "JWT";

    // Base64url text, or null for anything that is not: the decoder throws rather than answering
    // false on a character outside the alphabet, and a token is outside input.
    public static byte[]? Decode(string text)
    {
        try
        {
            return text.Length > 0 ? Base64Url.DecodeFromChars(text) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}