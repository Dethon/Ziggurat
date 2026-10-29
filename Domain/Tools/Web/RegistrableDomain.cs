using System.Globalization;
using System.Net;
using System.Reflection;

namespace Domain.Tools.Web;

// The site a host belongs to — what a login is a login to — by the public suffix list
// (publicsuffix.org), vendored as public_suffix_list.dat and refreshed by hand. Both of its
// sections count: a private suffix such as github.io is where unrelated people's sites share a
// host, and grouping them as one would hand one site's cookies to another's login. A host with
// no registrable domain — an address, a single label, a suffix on its own — is its own site.
public static class RegistrableDomain
{
    private static readonly Lazy<Rules> _rules = new(Rules.Load);
    private static readonly IdnMapping _idn = new();

    public static string Of(string host)
    {
        var name = host.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0 || IPAddress.TryParse(name.Trim('[', ']'), out _))
        {
            return name;
        }

        var labels = name.Split('.');
        if (labels.Length < 2)
        {
            return name;
        }

        // The rules are held in their ASCII spelling, so a host is matched as ASCII and answered
        // in the spelling it was asked in: a cookie's domain arrives punycoded, a typed url may not.
        var ascii = Ascii(name)?.Split('.');
        if (ascii is null || ascii.Length != labels.Length)
        {
            return name;
        }

        var suffixLabels = _rules.Value.SuffixLength(ascii);
        return suffixLabels >= labels.Length ? name : string.Join('.', labels[^(suffixLabels + 1)..]);
    }

    private static string? Ascii(string name)
    {
        try
        {
            return _idn.GetAscii(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed class Rules(HashSet<string> exact, HashSet<string> wildcard, HashSet<string> exceptions)
    {
        // How many trailing labels form the host's public suffix: an exception rule wins and takes
        // its own labels less the leftmost; otherwise the longest matching rule, a wildcard
        // covering one label more than it names; and with none, the default rule — the last label.
        public int SuffixLength(string[] labels)
        {
            var longest = 1;
            for (var take = labels.Length; take >= 1; take--)
            {
                var candidate = string.Join('.', labels[^take..]);
                if (exceptions.Contains(candidate))
                {
                    return take - 1;
                }

                if (take > longest && (exact.Contains(candidate)
                                       || (take > 1 && wildcard.Contains(string.Join('.', labels[^(take - 1)..])))))
                {
                    longest = take;
                }
            }

            return longest;
        }

        public static Rules Load()
        {
            using var stream = typeof(RegistrableDomain).Assembly
                                   .GetManifestResourceStream("Domain.Tools.Web.public_suffix_list.dat")
                               ?? throw new InvalidOperationException("The public suffix list is not embedded in Domain.");
            using var reader = new StreamReader(stream);
            HashSet<string> exact = [];
            HashSet<string> wildcard = [];
            HashSet<string> exceptions = [];
            while (reader.ReadLine() is { } line)
            {
                var rule = line.Trim().Split(' ', '\t')[0];
                if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // IdnMapping refuses '*' and '!', so the marker comes off before the conversion.
                var (set, body) = rule switch
                {
                    _ when rule.StartsWith("*.", StringComparison.Ordinal) => (wildcard, rule[2..]),
                    _ when rule.StartsWith('!') => (exceptions, rule[1..]),
                    _ => (exact, rule)
                };
                if (Ascii(body.ToLowerInvariant()) is { } ascii)
                {
                    set.Add(ascii);
                }
            }

            return new Rules(exact, wildcard, exceptions);
        }
    }
}