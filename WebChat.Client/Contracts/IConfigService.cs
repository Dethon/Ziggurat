using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IConfigService
{
    Task<AppConfig> GetConfigAsync();
}