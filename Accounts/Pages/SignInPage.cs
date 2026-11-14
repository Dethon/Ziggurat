using Accounts.Settings;

namespace Accounts.Pages;

public static class SignInPage
{
    public const string Path = "/sign-in";

    // On the public base rather than relative, because the session check answers for every host
    // the proxy protects and sign-in is served on one of them.
    // signedOut: the person has just signed out here, which the page says, and it waits to be asked
    // rather than offering the passkey again at once.
    public static string Url(AccountsSettings settings, string? returnUrl, bool signedOut = false)
    {
        var query = new[]
        {
            string.IsNullOrEmpty(returnUrl) ? null : $"returnUrl={Uri.EscapeDataString(returnUrl)}",
            signedOut ? "signedOut=1" : null
        }.OfType<string>().ToList();
        return new Uri(settings.PublicBase, $"{AccountsApp.PathBase.TrimStart('/')}{Path}")
               + (query.Count == 0 ? "" : "?" + string.Join('&', query));
    }
}