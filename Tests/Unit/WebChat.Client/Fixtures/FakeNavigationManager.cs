using Microsoft.AspNetCore.Components;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeNavigationManager : NavigationManager
{
    private readonly List<string> _navigatedTo = [];

    public FakeNavigationManager(string uri = "http://localhost/") => Initialize("http://localhost/", uri);

    public IReadOnlyList<string> NavigatedTo => _navigatedTo;

    protected override void NavigateToCore(string uri, bool forceLoad) => _navigatedTo.Add(uri);

    // The base implementation throws for anything but the two-argument overload, and
    // NavigateTo(uri, replace: true) goes through this one.
    protected override void NavigateToCore(string uri, NavigationOptions options) => _navigatedTo.Add(uri);
}