using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace McpChannelTelegram.Services;

// The pump, and nothing more: it reads updates and hands each one on. The album buffer beside it
// owns the only clock, and the intake owns every rule that turns a message into attachments.
public sealed class TelegramBotService : BackgroundService
{
    private const int PollTimeoutSeconds = 30;

    private readonly BotRegistry _botRegistry;
    private readonly ChannelNotificationEmitter _notificationEmitter;
    private readonly ApprovalCallbackRouter _approvalCallbackRouter;
    private readonly IAgentCatalog _agentCatalog;
    private readonly VoiceNoteDictation _dictation;
    private readonly TelegramGate _gate;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly AlbumBuffer _albums;

    public TelegramBotService(
        BotRegistry botRegistry,
        ChannelNotificationEmitter notificationEmitter,
        ApprovalCallbackRouter approvalCallbackRouter,
        IAgentCatalog agentCatalog,
        VoiceNoteDictation dictation,
        TelegramGate gate,
        TimeProvider timeProvider,
        ILogger<TelegramBotService> logger)
    {
        _botRegistry = botRegistry;
        _notificationEmitter = notificationEmitter;
        _approvalCallbackRouter = approvalCallbackRouter;
        _agentCatalog = agentCatalog;
        _dictation = dictation;
        _gate = gate;
        _logger = logger;
        _albums = new AlbumBuffer(timeProvider, ReleaseAlbumAsync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telegram bot polling started");

        var pollers = _botRegistry.GetAllBots()
            .Select(b => PollBotAsync(b.AgentId, b.Client, stoppingToken))
            .ToArray();

        await Task.WhenAll(pollers);

        _logger.LogInformation("Telegram bot polling stopped");
    }

    private async Task PollBotAsync(string agentId, ITelegramBotClient botClient, CancellationToken stoppingToken)
    {
        int? offset = null;

        _logger.LogInformation("Started polling for agent {AgentId}", agentId);

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
                _logger.LogError(ex, "Telegram polling error for agent {AgentId}: {Message}", agentId, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        _logger.LogInformation("Stopped polling for agent {AgentId}", agentId);
    }

    private async Task ProcessUpdateAsync(string agentId, ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is not null)
        {
            await _approvalCallbackRouter.HandleCallbackQueryAsync(botClient, update.CallbackQuery, cancellationToken);
            return;
        }

        if (update.Message is not { } message)
        {
            return;
        }

        // An album arrives as one update per file. It is held until the group goes quiet, so the
        // turn carries every reference and the caption, wherever in the group it landed.
        if (message.MediaGroupId is not null)
        {
            _albums.Add(agentId, botClient, message);
            return;
        }

        await HandleMessagesAsync(agentId, botClient, [message], cancellationToken);
    }

    // The buffer cannot tell a sender to try again, so a released group must never throw back at
    // it. It also runs uncancelled: the group was already acknowledged to Telegram, and dropping
    // it because the pump has stopped would lose files nobody can resend.
    private async Task ReleaseAlbumAsync(Album album)
    {
        try
        {
            await HandleMessagesAsync(album.AgentId, album.Client, album.Messages, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle a released Telegram album for agent {AgentId}", album.AgentId);
        }
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

        var intake = AttachmentIntake.Read(agentId, messages);

        // A voice note is the one piece of media that becomes words rather than a file, so it is
        // read here and not by the intake, which mints references.
        var voice = messages.Select(m => m.Voice).OfType<Voice>().FirstOrDefault();

        // A message with neither words nor files is not a turn: a service message in a forum
        // thread qualifies under the addressing rule and must still cost nothing.
        if (content.Length == 0 && voice is null && intake.Attachments.Count == 0 && intake.Refusals.Count == 0)
        {
            return;
        }

        // Admitted by the account the sender's numeric id is linked to, never by a username, which
        // is its owner's to change and Telegram's to reassign, or a chat's name or a first name,
        // which are anybody's to pick. Anything else the gate has answered already.
        if (await _gate.AdmitAsync(botClient, first, content, cancellationToken) is not { } person)
        {
            return;
        }

        // The turn's sender stays the username, which memory and the identity map are keyed by.
        // A person with no username has nothing else stable to go by, so their id stands in.
        var from = first.From!;
        var sender = from.Username ?? $"{from.Id}";
        _approvalCallbackRouter.RecordSender(sender, from.Id);

        var chatId = first.Chat.Id;
        var conversationId = ConversationIdentity.ForChat(chatId, first.MessageThreadId).ConversationId;

        _botRegistry.RegisterChatAgent(chatId, agentId);

        var turnStops = await ReportRefusalsAsync(agentId, botClient, first, intake, cancellationToken);

        // Nothing survived, or the model cannot read what did, so the reply is the whole response.
        if (turnStops || (content.Length == 0 && voice is null && intake.Attachments.Count == 0))
        {
            return;
        }

        // Transcribed after the authorisation check, so a stranger's voice note costs no download
        // and no transcription. A caption is words the person said too, so both reach the agent:
        // the caption, a newline, then the transcript.
        if (voice is not null)
        {
            switch (await _dictation.ReadAsync(botClient, voice, cancellationToken))
            {
                // No turn, even with a caption: an answer to half of what someone said is worse
                // than saying the other half could not be made out.
                case Dictation.Refused refused:
                    await botClient.SendMessage(
                        first.Chat.Id, refused.Reply, replyParameters: first.MessageId,
                        cancellationToken: cancellationToken);
                    return;

                case Dictation.Words words:
                    content = string.Join(
                        "\n", new List<string> { content, words.Transcript }.Where(part => part.Length > 0));
                    break;
            }
        }

        // Unlike Schedule/Library (a durable record
        // that simply stays due), Telegram has no channel-level way to signal "try again later" back
        // to the sender — so nothing here gates on liveness. The buffer-always policy targets the
        // well-known "channel-telegram" subscriber id and creates its queue on demand, so buffering
        // holds unconditionally: through a disconnect (PruneIdle only evicts an empty, hour-idle
        // subscriber), and even before the agent's first poll after a server restart or an idle
        // eviction. A late reconnect still delivers, bounded only by the inbox capacity. The emit's
        // return value is read for the warning alone.
        var live = await _notificationEmitter.EmitAsync(
            new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = sender,
                Content = content,
                AgentId = agentId,
                // The turn acts for the account; its user id stays the sender, the name the approval
                // router and the identity map still know the person by.
                Principal = new Principal(TelegramAccess.Channel, sender, person.Account),
                Attachments = intake.Attachments.Count > 0 ? intake.Attachments : null,
                Timestamp = DateTimeOffset.UtcNow
            },
            cancellationToken);

        if (!live)
        {
            _logger.LogWarning(
                "No live channel_receive subscriber; buffering message from {Sender} for later delivery", sender);
        }

        _logger.LogDebug("Emitted message notification for conversation {ConversationId} from {Sender} (agent: {AgentId})",
            conversationId, sender, agentId);
    }

    // Every refusal for one message in a single reply quoting the item that failed and naming its
    // file, the way the gate answers a stranger. Answers whether the turn stops:
    // the two grounds the intake found are properties of one file and drop only that file, while a
    // model that cannot read what was attached would answer as though nothing had been sent, and
    // an answer that silently ignores the question is worse than no answer.
    private async Task<bool> ReportRefusalsAsync(
        string agentId,
        ITelegramBotClient botClient,
        Message first,
        Intake intake,
        CancellationToken cancellationToken)
    {
        // The same resolution WebChat asks, from the same catalogue shape, so the two channels
        // cannot disagree about which model is refusing. Permissive wherever the catalogue is
        // silent — a cold start, or a blip at the provider, must not remove the feature. Telegram
        // has no per-message model override, so the model a turn runs on is the agent's default.
        var capabilityRefusal = intake.Attachments.Count > 0
            ? AttachmentCapability.Refusal(
                _agentCatalog.Get(agentId), null, intake.Attachments.Select(a => a.MediaType))
            : null;

        var reasons = intake.Refusals
            .Select(refusal => refusal.Reason)
            .Concat(capabilityRefusal is null ? [] : [capabilityRefusal])
            .ToList();

        if (reasons.Count > 0)
        {
            await botClient.SendMessage(
                first.Chat.Id,
                string.Join("\n", reasons),
                replyParameters: intake.Refusals.Count > 0 ? intake.Refusals[0].MessageId : first.MessageId,
                cancellationToken: cancellationToken);
        }

        return capabilityRefusal is not null;
    }

    // Unchanged from the day this channel only took text, with the caption standing in for it: a
    // command prefix, or any message in a forum thread. A media message therefore qualifies under
    // exactly the same rule, and one with an empty caption is a legitimate turn.
    private static bool IsBotMessage(string content, IReadOnlyList<Message> messages) =>
        content.StartsWith('/') || messages.Any(m => m.MessageThreadId.HasValue);

    public override void Dispose()
    {
        _albums.Dispose();
        base.Dispose();
    }
}