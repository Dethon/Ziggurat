namespace Accounts.Pages;

// What a person calls a signed-in device: the browser and the system it runs on, read off the user
// agent the session began with. Order matters in both lists — every browser below claims to be Safari
// and most claim Chrome, an iPhone says "like Mac OS X" and Android says "Linux" — so the more
// particular name is looked for first. A user agent it cannot place is shown as it was sent, never
// guessed at.
public static class DeviceNames
{
    private static readonly (string Token, string Name)[] _browsers =
    [
        ("Edg/", "Edge"), ("EdgA/", "Edge"), ("EdgiOS/", "Edge"),
        ("SamsungBrowser/", "Samsung Internet"),
        ("OPR/", "Opera"),
        ("FxiOS/", "Firefox"), ("Firefox/", "Firefox"),
        ("CriOS/", "Chrome"), ("HeadlessChrome/", "Chrome"), ("Chrome/", "Chrome"),
        ("Version/", "Safari")
    ];

    private static readonly (string Token, string Name)[] _systems =
    [
        ("iPhone", "iPhone"), ("iPad", "iPad"),
        ("Android", "Android"),
        ("Windows", "Windows"),
        ("CrOS", "ChromeOS"),
        ("Macintosh", "Mac"),
        ("Linux", "Linux")
    ];

    public static string Of(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown device";
        }

        var browser = First(_browsers, userAgent);
        var system = First(_systems, userAgent);
        return browser is not null && system is not null ? $"{browser} on {system}" : userAgent;
    }

    private static string? First((string Token, string Name)[] names, string userAgent) =>
        names.FirstOrDefault(n => userAgent.Contains(n.Token, StringComparison.Ordinal)).Name;
}