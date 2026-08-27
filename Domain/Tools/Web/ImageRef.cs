namespace Domain.Tools.Web;

// A handle standing for one picture on a browsed page. Its own namespace, beside but never inside
// the e- refs the accessibility snapshot assigns: a ref's shape is what tells a tool whether the
// request was meant for it, so view_image can refuse e-3 by name and web_action can refuse i-3.
//
// One definition, because two would be two answers to "is this mine?".
public static class ImageRef
{
    public const string Prefix = "i-";

    public static string For(int number) => $"{Prefix}{number}";

    public static bool IsImageRef(string? candidate) =>
        candidate is not null
        && candidate.StartsWith(Prefix, StringComparison.Ordinal)
        && candidate.Length > Prefix.Length
        && candidate[Prefix.Length..].All(char.IsAsciiDigit);
}