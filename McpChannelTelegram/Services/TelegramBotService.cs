using Domain.Contracts;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpChannelTelegram.Settings;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.Services;

public sealed class TelegramBotService(
    BotRegistry botRegistry,
    ChannelSettings settings,
    ChannelNotificationEmitter notificationEmitter,
    ApprovalCallbackRouter approvalCallbackRouter,
    IAgentCatalog agentCatalog,
    TimeProvider timeProvider,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private const int PollTimeoutSeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram bot polling started. Allowed usernames: {Usernames}",
            string.Join(", ", settings.AllowedUsernames));

        var pollers = botRegistry.GetAllBots()
            .Select(b => PollBotAsync(b.AgentId, b.Client, stoppingToken))
            .ToArray();

        await Task.WhenAll(pollers);

        logger.LogInformation("Telegram bot polling stopped");
    }

    private async Task PollBotAsync(string agentId, ITelegramBotClient botClient, CancellationToken stoppingToken)
    {
        int? offset = null;

        logger.LogInformation("Started polling for agent {AgentId}", agentId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await botClient.GetUpdates(
                    offset: offset,
                    timeout: PollTimeoutSeconds,
                    cancellationToken: stoppingToken);

                offset = updates
                    .Select(u => u.Id + 1)
                    .Cast<int?>()
                    .DefaultIfEmpty(null)
                    .Max() ?? offset;

                foreach (var update in updates)
                {
                    await ProcessUpdateAsync(agentId, botClient, update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram polling error for agent {AgentId}: {Message}", agentId, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        logger.LogInformation("Stopped polling for agent {AgentId}", agentId);
    }

    private async Task ProcessUpdateAsync(string agentId, ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is not null)
        {
            await approvalCallbackRouter.HandleCallbackQueryAsync(botClient, update.CallbackQuery, cancellationToken);
            return;
        }

        if (update.Message is not { } message)
        {
            return;
        }

        await HandleMessagesAsync(agentId, botClient, [message], cancellationToken);
    }

    private async Task HandleMessagesAsync(
        string agentId,
        ITelegramBotClient botClient,
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken)
    {
        var first = messages[0];
        var content = messages
            .Select(m => m.Text ?? m.Caption)
            .FirstOrDefault(text => !string.IsNullOrEmpty(text)) ?? string.Empty;

        if (!IsBotMessage(content, messages))
        {
            return;
        }

        var attachments = AttachmentIntake.Read(agentId, messages);

        // A message with neither words nor files is not a turn: a service message in a forum
        // thread qualifies under the addressing rule and must still cost nothing.
        if (content.Length == 0 && attachments.Count == 0)
        {
            return;
        }

        var sender = first.From?.Username
                     ?? first.Chat.Username
                     ?? first.Chat.FirstName
                     ?? $"{first.Chat.Id}";

        if (!settings.AllowedUsernames.Contains(sender))
        {
            await botClient.SendMessage(
                first.Chat.Id,
                "You are not authorized to use this bot.",
                replyParameters: first.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        var chatId = first.Chat.Id;
        var threadId = first.MessageThreadId ?? chatId;
        var conversationId = $"{chatId}:{threadId}";

        botRegistry.RegisterChatAgent(chatId, agentId);

        // Unlike ServiceBus (broker-level abandon/redeliver) or Schedule/Library (a durable record
        // that simply stays due), Telegram has no channel-level way to signal "try again later" back
        // to the sender — so nothing here gates on liveness. The buffer-always policy targets the
        // well-known "channel-telegram" subscriber id and creates its queue on demand, so buffering
        // holds unconditionally: through a disconnect (PruneIdle only evicts an empty, hour-idle
        // subscriber), and even before the agent's first poll after a server restart or an idle
        // eviction. A late reconnect still delivers, bounded only by the inbox capacity. The emit's
        // return value is read for the warning alone.
        var live = await notificationEmitter.EmitAsync(
            new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = sender,
                Content = content,
                AgentId = agentId,
                Attachments = attachments.Count > 0 ? attachments : null,
                Timestamp = DateTimeOffset.UtcNow
            },
            cancellationToken);

        if (!live)
        {
            logger.LogWarning(
                "No live channel_receive subscriber; buffering message from {Sender} for later delivery", sender);
        }

        logger.LogDebug("Emitted message notification for conversation {ConversationId} from {Sender} (agent: {AgentId})",
            conversationId, sender, agentId);
    }

    // Unchanged from the day this channel only took text, with the caption standing in for it: a
    // command prefix, or any message in a forum thread. A media message therefore qualifies under
    // exactly the same rule, and one with an empty caption is a legitimate turn.
    private static bool IsBotMessage(string content, IReadOnlyList<Message> messages) =>
        content.StartsWith('/') || messages.Any(m => m.MessageThreadId.HasValue);
}