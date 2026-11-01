using Microsoft.AspNetCore.Components;

namespace WebChat.Client.Layout;

// The household's admin area: its pages, in the order its menu lists them. Each is two segments
// under /admin, so none takes a space's slug, which the chat's /{Slug} reads off the first segment.
public static class AdminArea
{
    public const string Settings = "/admin/settings";
    public const string Agents = "/admin/agents";
    public const string Members = "/admin/members";

    public const string Home = Settings;

    public static IReadOnlyList<(string Path, string Label)> Pages { get; } = [(Settings, "Settings"), (Agents, "Agents"), (Members, "Members")];

    // Into the admin area and back out by a full load, never an in-app navigation: the chat page
    // starts its live connection once per load, and coming back to it in-app — the browser's back
    // button included — would start it a second time.
    public static void Enter(NavigationManager navigation, string path) => navigation.NavigateTo(path, forceLoad: true);

    public static void BackToChat(NavigationManager navigation) => navigation.NavigateTo("/", forceLoad: true);
}