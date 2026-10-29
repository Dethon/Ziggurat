using System.Net.Http.Json;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

public sealed class ConfigService(HttpClient httpClient) : IConfigService
{
    private AppConfig? _config;

    public async Task<AppConfig> GetConfigAsync()
    {
        return _config ??= await httpClient.GetFromJsonAsync<AppConfig>("/api/config")
            ?? new AppConfig(null);
    }

    public async Task<SpaceConfig?> GetSpaceAsync(string slug)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<SpaceConfig>($"/api/spaces/{Uri.EscapeDataString(slug)}");
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}