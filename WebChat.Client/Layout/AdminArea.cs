using Microsoft.AspNetCore.Components;

namespace WebChat.Client.Layout;

// The household's admin area: its pages, in the order its menu lists them. Each is two segments
// under /admin, so none takes a space's slug, which the chat's /{Slug} reads off the first segment.
public static class AdminArea
{
    public const string Settings = "/admin/settings";
    public const string Keys = "/admin/keys";
    public const string Agents = "/admin/agents";
    public const string Features = "/admin/features";
    public const string Spaces = "/admin/spaces";

    public const string Home = Settings;

    public static IReadOnlyList<(string Path, string Label)> Pages { get; } =
        [(Settings, "Settings"), (Keys, "Keys"), (Agents, "Agents"), (Features, "Features"), (Spaces, "Spaces")];

    // Whether the page showing is one of the area's own.
    public static bool Contains(NavigationManager navigation) =>
        navigation.ToBaseRelativePath(navigation.Uri).StartsWith("admin/", StringComparison.Ordinal);

    // Into the admin area and back out by a full load, never an in-app navigation: the chat page
    // starts its live connection once per load, and coming back to it in-app — the browser's back
    // button included — would start it a second time.
    public static void Enter(NavigationManager navigation, string path) => navigation.NavigateTo(path, forceLoad: true);

    public static void BackToChat(NavigationManager navigation) => navigation.NavigateTo("/", forceLoad: true);
}