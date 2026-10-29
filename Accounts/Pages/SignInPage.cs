using Accounts.Settings;

namespace Accounts.Pages;

public static class SignInPage
{
    public const string Path = "/sign-in";

    // On the public base rather than relative, because the session check answers for every host
    // the proxy protects and sign-in is served on one of them.
    public static string Url(AccountsSettings settings, string? returnUrl) =>
        new Uri(settings.PublicBase, $"{AccountsApp.PathBase.TrimStart('/')}{Path}").ToString()
        + (string.IsNullOrEmpty(returnUrl) ? "" : $"?returnUrl={Uri.EscapeDataString(returnUrl)}");
}