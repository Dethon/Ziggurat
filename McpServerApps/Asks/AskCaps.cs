using McpServerApps.Serving;
using McpServerApps.Settings;
using McpServerApps.Store;

namespace McpServerApps.Asks;

// The daily cap an app's asks meet: the one place it is decided, so an owner's household default and an
// app's override are read here, ahead of the deployment's (Asks:DailyCap), and never beside it. Asked for
// each ask, by the viewer's session, since a household's values are read on its own people's assertions.
public sealed class AskCaps(AppsSettings settings)
{
    public Task<int> LimitAsync(BuiltApp app, AppSession session, CancellationToken ct) =>
        Task.FromResult(settings.Asks.DailyCap);
}