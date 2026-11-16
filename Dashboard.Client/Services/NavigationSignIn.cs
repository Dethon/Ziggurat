using Dashboard.Client.Contracts;
using Microsoft.AspNetCore.Components;

namespace Dashboard.Client.Services;

// The page opened again as a full load: the service worker lets a navigation reach the network, so the
// proxy answers it — sign-in on the main host for a page nobody is signed in to — rather than the cached
// app answering itself.
public sealed class NavigationSignIn(NavigationManager navigation) : ISignIn
{
    public void SendToSignIn() => navigation.NavigateTo(navigation.Uri, forceLoad: true);
}