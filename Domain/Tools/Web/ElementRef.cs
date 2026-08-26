namespace Domain.Tools.Web;

// A handle standing for one interactive element in an accessibility snapshot. The counterpart of
// ImageRef: one shape rule — letter, dash, number — across both namespaces, so a ref's shape alone
// says which tool it was meant for.
public static class ElementRef
{
    public const string Prefix = "e-";

    public static string For(int number) => $"{Prefix}{number}";

    public static bool IsElementRef(string? candidate) =>
        candidate is not null
        && candidate.StartsWith(Prefix, StringComparison.Ordinal)
        && candidate.Length > Prefix.Length
        && candidate[Prefix.Length..].All(char.IsAsciiDigit);
}