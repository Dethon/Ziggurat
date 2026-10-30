using Accounts.Settings;

namespace Accounts.Pages;

public static class ReturnUrls
{
    // Where a finished ceremony sends the browser: the address it came from when that is one of the
    // deployment's own, and the front page otherwise. A path is this host's; an absolute address
    // must be on an allowed origin, or a crafted link would hand a fresh sign-in to another site.
    public static string Resolve(AccountsSettings settings, string? returnUrl)
    {
        var front = settings.PublicBase.ToString();
        if (string.IsNullOrEmpty(returnUrl))
        {
            return front;
        }

        if (returnUrl.StartsWith('/'))
        {
            return returnUrl.StartsWith("//", StringComparison.Ordinal) || returnUrl.StartsWith("/\\", StringComparison.Ordinal)
                ? front
                : returnUrl;
        }

        return Uri.TryCreate(returnUrl, UriKind.Absolute, out var url)
               && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
               && settings.AllowedOrigins.Any(origin =>
                   string.Equals(origin.TrimEnd('/'), url.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            ? url.ToString()
            : front;
    }
}