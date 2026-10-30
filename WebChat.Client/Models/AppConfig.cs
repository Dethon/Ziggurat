namespace WebChat.Client.Models;

public record AppConfig(string? AgentUrl, string? VapidPublicKey = null);