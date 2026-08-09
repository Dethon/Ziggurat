using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

// The upload store lives on the channel server, which is where the hub lives too — never on the
// host that served this page. Both directions resolve their address the same way the hub
// connection resolves its own: same origin behind the reverse proxy, the configured agent URL
// otherwise. One place, so an upload and a download cannot disagree about where the store is.
public sealed class AttachmentEndpointResolver(
    IConfigService configService,
    NavigationManager navigationManager)
{
    public async Task<string> ResolveAsync(string serverRelativePath)
    {
        var config = await configService.GetConfigAsync();
        var isHttps = navigationManager.BaseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var path = "/" + serverRelativePath.TrimStart('/');

        return string.IsNullOrEmpty(config.AgentUrl) || isHttps
            ? navigationManager.ToAbsoluteUri(path).ToString()
            : $"{config.AgentUrl.TrimEnd('/')}{path}";
    }
}