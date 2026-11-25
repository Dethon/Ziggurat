namespace Domain.Apps;

// The app sign-in's wire, spelled once for its two ends. A navigation on an app origin with no app
// session is sent to the accounts service's route on the main host (Path), carrying where it was
// going (ReturnUrlParameter); the service checks the person's own session there and sends them back
// to the origin's callback (CallbackPath) with a single-use code bound to that origin, which the apps
// server redeems service to service. A return is believed only onto a built app's origin under the
// apps domain, and the callback goes on only to a path of its own origin, so no crafted link carries a
// code, or a signed-in person, anywhere else.
public static class AppSignIn
{
    public const string Path = "/accounts/app-sign-in";
    public const string ReturnUrlParameter = "returnUrl";

    // Under the apps server's own /_app/, which no file of an app's can shadow.
    public const string CallbackPath = "/_app/sign-in";
    public const string CodeParameter = "code";
    public const string PathParameter = "path";

    // The built app's origin an address is on — scheme, host and port, lower-cased — and the path it
    // asked for, or none when the address is not a built app's under the apps domain. https always;
    // plain http only under a loopback name (.localhost), where nothing between the browser and the
    // server can read the code. No apps domain returns to nothing.
    public static AppReturn? ReturnTo(string? url, string appsDomain)
    {
        if (string.IsNullOrWhiteSpace(appsDomain)
            || !Uri.TryCreate(url, UriKind.Absolute, out var address)
            || address.UserInfo.Length > 0
            || !(address.Scheme == Uri.UriSchemeHttps || (address.Scheme == Uri.UriSchemeHttp && IsLoopbackName(appsDomain)))
            || AppOrigin.Parse(address.Authority, appsDomain) is null)
        {
            return null;
        }

        return new AppReturn(address.GetLeftPart(UriPartial.Authority).ToLowerInvariant(), address.PathAndQuery);
    }

    // Where the accounts service sends the person back with their code.
    public static string Callback(string origin, string code, string path) =>
        $"{origin}{CallbackPath}?{CodeParameter}={Uri.EscapeDataString(code)}&{PathParameter}={Uri.EscapeDataString(path)}";

    // A path on the callback's own origin: exactly one leading '/', since a browser reads `//host` and
    // `/\host` as another site's. Anything else is the app's front page.
    public static string SamePath(string? path) =>
        path is ['/', ..] && !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("/\\", StringComparison.Ordinal)
            ? path
            : "/";

    private static bool IsLoopbackName(string appsDomain)
    {
        var domain = appsDomain.TrimEnd('.').ToLowerInvariant();
        return domain == "localhost" || domain.EndsWith(".localhost", StringComparison.Ordinal);
    }
}

public sealed record AppReturn(string Origin, string Path);