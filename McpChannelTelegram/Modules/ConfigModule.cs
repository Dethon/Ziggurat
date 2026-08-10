using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;

namespace McpChannelTelegram.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureChannel(this IServiceCollection services, ChannelSettings settings)
    {
        services
            .AddSingleton(new BotRegistry(settings.Bots))
            .AddSingleton<MessageAccumulator>()
            .AddSingleton<ApprovalCallbackRouter>()
            .AddSingleton(TimeProvider.System)
            // The catalogue exists so the attachment capability resolution has something to ask.
            // Nothing else on this channel consumes it.
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddHostedService<TelegramBotService>();

        services
            .AddMcpHost(settings)
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            .WithTools<FetchAttachmentTool>()
            // Buffer-always: Telegram has no channel-level way to tell a sender "try again later",
            // so a message arriving during a cold start or just after an idle eviction must be
            // buffered rather than fanned out to nobody. The target is the id McpChannelConnection
            // derives for itself; a mismatch would buffer into a queue nobody drains.
            .AddChannelServer(
                DeliveryPolicy.BufferAlways,
                ChannelProtocol.ChannelClientNamePrefix + "telegram");

        return services;
    }
}