namespace Domain.Tools.Web;

// The site a host belongs to — what a login is a login to. A heuristic, not the public suffix
// list: the last two labels, or three under a two-letter country code whose second level is one
// of the generic ones ("bbc.co.uk", "anz.com.au"). It is asked to group a handoff's hosts with the
// cookies and storage they left, where a miss only keeps a remembered site's sibling cookie or
// forgets one too many; it never decides who may reach what.
public static class RegistrableDomain
{
    private static readonly HashSet<string> _genericSecondLevels = ["co", "com", "net", "org", "gov", "ac", "edu"];

    public static string Of(string host)
    {
        var labels = host.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant().Split('.');
        if (labels.Length <= 2 || labels.All(label => label.All(char.IsAsciiDigit)))
        {
            return string.Join('.', labels);
        }

        var takeThree = labels[^1].Length == 2 && _genericSecondLevels.Contains(labels[^2]);
        return string.Join('.', labels[^(takeThree ? 3 : 2)..]);
    }
}