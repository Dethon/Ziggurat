using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Accounts.Store;

public static class Tokens
{
    // An id is an opaque key that other stores will file things under (memory, voice profiles), so
    // it is lowercase hex: no ':' to break a composite key, nothing a file name or a header refuses.
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    // A bearer secret: a link's token or a session cookie's value.
    public static string NewSecret() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}