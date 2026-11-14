using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Transcription;
using Infrastructure.Metrics;
using Mcp.Hosting;
using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;
using StackExchange.Redis;

namespace McpChannelTelegram.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureChannel(this IServiceCollection services, ChannelSettings settings)
    {
        services
            .AddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
            .AddMetricsPublishing("mcp-channel-telegram")
            .AddSingleton(settings.Bots)
            .AddSingleton<BotRegistry>()
            .AddSingleton(settings.Dictation)
            .AddLemonadeTranscription(settings.Dictation.Transcription)
            .AddSingleton<VoiceNoteDictation>()
            .AddSingleton<MessageAccumulator>()
            .AddSingleton<ApprovalCallbackRouter>()
            .AddSingleton(TimeProvider.System)
            // Who a sender is, asked of the accounts service with this server's own credential.
            .AddAccountResolver(settings.Accounts, settings.Mcp.SharedSecret)
            .AddSingleton(settings.LinkCache)
            .AddSingleton<TelegramAccess>()
            .AddSingleton<TelegramGate>()
            // Every household's bots, and the names of a person's household's agents for /agent, asked
            // of the accounts service with this server's own credential (.claude/rules/household-config.md).
            .AddHouseholdConfiguration(settings.Accounts, settings.Mcp.SharedSecret)
            .AddSingleton<IChatAgentChoices, RedisChatAgentChoices>()
            .AddSingleton<ChatAgents>()
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
            .WithTools<OfferApprovalTool>()
            .WithTools<SettleApprovalTool>()
            .WithTools<OfferAlertTool>()
            .WithTools<SettleAlertTool>()
            // Telegram has no agent picker and no per-message model override, so this catalogue
            // exists for one reason: the attachment capability resolution needs something to ask
            // about the model a turn will run on. It arrives through the registration the agent
            // already performs on connect and on every reconnect, so no new protocol call is added.
            .WithTools<RegisterAgentsTool>()
            .WithTools<FetchAttachmentTool>()
            // A fire whose author asked for it here opens their private chat (CreateConversationTool).
            .WithTools<CreateConversationTool>()
            // Buffer-always: Telegram has no channel-level way to tell a sender "try again later",
            // so a message arriving during a cold start or just after an idle eviction must be
            // buffered rather than fanned out to nobody. The target is the id McpChannelConnection
            // derives for itself; a mismatch would buffer into a queue nobody drains.
            .AddChannelServer(
                DeliveryPolicy.BufferAlways,
                ChannelProtocol.ChannelClientNamePrefix + ChannelIds.Telegram);

        return services;
    }
}