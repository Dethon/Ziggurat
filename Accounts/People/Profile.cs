namespace Accounts.People;

// What a person shows the household: the name they go by and the picture beside it. Theirs to set on
// the account page, and the household migration's to carry over from the retired chat client's list;
// both are held to the same reading here.
public sealed record Profile(string DisplayName, string? AvatarUrl)
{
    // The columns' own widths (AccountsDbContext), which SQLite would not enforce.
    public const int DisplayNameLength = 200;
    public const int AvatarUrlLength = 2000;

    // Trimmed, an empty avatar meaning none, or why it cannot be either.
    public static (Profile? Profile, string? Problem) Read(string? displayName, string? avatarUrl)
    {
        var name = displayName?.Trim() ?? "";
        var avatar = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim();
        return name.Length == 0
            ? (null, "A name is needed.")
            : name.Length > DisplayNameLength
                ? (null, $"A name is at most {DisplayNameLength} characters.")
                : avatar is not null && !IsAvatar(avatar)
                    ? (null, "A picture is a web address (https://…) or a path on this site (/…).")
                    : (new Profile(name, avatar), null);
    }

    // Every app draws the avatar as an image on its own page, so it is an http(s) address or a path
    // on the deployment's hosts and nothing else: no script or data scheme, and no "//" or "\" a
    // browser would read as somebody else's host — nor whitespace, which a browser strips from an
    // address before reading it, making "/\t/host" the same "//host". A path is tested first, since
    // Uri reads "/avatars/x.png" as an absolute file address on Linux.
    private static bool IsAvatar(string avatar) =>
        avatar.Length <= AvatarUrlLength
        && !avatar.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')
        && (avatar.StartsWith('/')
            ? !avatar.StartsWith("//", StringComparison.Ordinal)
            : Uri.TryCreate(avatar, UriKind.Absolute, out var uri)
              && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp));
}