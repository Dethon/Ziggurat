using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpChannelTelegram.Settings;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.Services;

// The pump, and nothing more: it keeps a poller on every bot and hands each update on. The album
// buffer beside it owns the only clock a message has, and the intake owns every rule that turns a
// message into attachments.
//
// The shared bot is polled from the start. The households' own bots are their configuration's, read
// across households at once (IHouseholdConfiguration.TelegramBotsAsync, which takes no proof: nothing
// has named a household yet) and again every BotSettings.Refresh, so a bot saved, changed or removed
// starts or stops within moments of the save being announced, and an accounts service that cannot
// answer leaves the bots already polling as they are.
public sealed class TelegramBotService : BackgroundService
{
    private const int PollTimeoutSeconds = 30;

    private readonly BotRegistry _botRegistry;
    private readonly ChannelNotificationEmitter _notificationEmitter;
    private readonly ApprovalCallbackRouter _approvalCallbackRouter;
    private readonly IAgentCatalog _agentCatalog;
    private readonly VoiceNoteDictation _dictation;
    private readonly TelegramGate _gate;
    private readonly ChatAgents _chatAgents;
    private readonly IHouseholdConfiguration _households;
    private readonly BotSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly AlbumBuffer _albums;
    private readonly Dictionary<string, Poller> _pollers = [];

    public TelegramBotService(
        BotRegistry botRegistry,
        ChannelNotificationEmitter notificationEmitter,
        ApprovalCallbackRouter approvalCallbackRouter,
        IAgentCatalog agentCatalog,
        VoiceNoteDictation dictation,
        TelegramGate gate,
        ChatAgents chatAgents,
        IHouseholdConfiguration households,
        BotSettings settings,
        TimeProvider timeProvider,
        ILogger<TelegramBotService> logger)
    {
        _botRegistry = botRegistry;
        _notificationEmitter = notificationEmitter;
        _approvalCallbackRouter = approvalCallbackRouter;
        _agentCatalog = agentCatalog;
        _dictation = dictation;
        _gate = gate;
        _chatAgents = chatAgents;
        _households = households;
        _settings = settings;
        _time = timeProvider;
        _logger = logger;
        _albums = new AlbumBuffer(timeProvider, ReleaseAlbumAsync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telegram bot polling started");

        try
        {
            using var refresh = new PeriodicTimer(_settings.Refresh, _time);
            do
            {
                await RefreshAsync(stoppingToken);
            } while (await refresh.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(_pollers.Values.Select(poller => poller.Polling));
        _pollers.Values.ToList().ForEach(poller => poller.Stop.Dispose());
        _logger.LogInformation("Telegram bot polling stopped");
    }

    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        try
        {
            _botRegistry.Replace(await _households.TelegramBotsAsync(stoppingToken));
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the households' Telegram bots; the bots already polling go on");
        }

        var current = _botRegistry.All;
        foreach (var gone in _pollers.Values.Where(poller => !current.Contains(poller.Bot)).ToList())
        {
            await gone.Stop.CancelAsync();
            await gone.Polling;
            gone.Stop.Dispose();
            _pollers.Remove(gone.Bot.Key);
        }

        foreach (var bot in current.Where(bot => !_pollers.ContainsKey(bot.Key)))
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _pollers[bot.Key] = new Poller(bot, stop, Task.Run(() => PollBotAsync(bot, stop.Token), CancellationToken.None));
        }
    }

    private async Task PollBotAsync(TelegramBot bot, CancellationToken stoppingToken)
    {
        int? offset = null;

        _logger.LogInformation("Started polling the bot {Bot}", bot.Key);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.Client.GetUpdates(
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
                    await ProcessUpdateAsync(bot, update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram polling error for the bot {Bot}: {Message}", bot.Key, ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Stopped polling the bot {Bot}", bot.Key);
    }

    private async Task ProcessUpdateAsync(TelegramBot bot, Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is not null)
        {
            await _approvalCallbackRouter.HandleCallbackQueryAsync(bot.Client, update.CallbackQuery, cancellationToken);
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
            _albums.Add(bot, message);
            return;
        }

        await HandleMessagesAsync(bot, [message], cancellationToken);
    }

    // The buffer cannot tell a sender to try again, so a released group must never throw back at
    // it. It also runs uncancelled: the group was already acknowledged to Telegram, and dropping
    // it because the pump has stopped would lose files nobody can resend.
    private async Task ReleaseAlbumAsync(Album album)
    {
        try
        {
            await HandleMessagesAsync(album.Bot, album.Messages, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle a released Telegram album for the bot {Bot}", album.Bot.Key);
        }
    }

    private async Task HandleMessagesAsync(
        TelegramBot bot,
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken)
    {
        var botClient = bot.Client;
        var first = messages[0];
        var content = messages
            .Select(m => m.Text ?? m.Caption)
            .FirstOrDefault(text => !string.IsNullOrEmpty(text)) ?? string.Empty;

        if (!IsBotMessage(content, messages))
        {
            return;
        }

        var chatId = first.Chat.Id;
        var conversationId = ConversationIdentity.ForChat(chatId, first.MessageThreadId).ConversationId;
        var intake = AttachmentIntake.Read(bot.Key, conversationId, messages);

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
        if (await _gate.AdmitAsync(bot, first, content, cancellationToken) is not { } person)
        {
            return;
        }

        // Who answers here, asked by somebody let in: a stranger is only ever shown the ways in.
        if (TelegramCommand.Of(content) is (ChatAgents.Command, var argument))
        {
            await botClient.SendMessage(
                chatId, await _chatAgents.AnswerAsync(bot, chatId, person, argument, cancellationToken),
                replyParameters: first.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // The turn's sender stays the username, the name the agent shows the model; a person with
        // no username has nothing else to go by, so their id stands in. Who the turn is, approvals
        // included, is the account the assertion names.
        var from = first.From!;
        var sender = from.Username ?? $"{from.Id}";

        // A household's bot is its agent's. On the shared bot the chat's pick answers, and a chat that
        // picked none names no agent, which the agent answers with the household's default.
        var agentId = bot.AgentId ?? await _chatAgents.ChosenAsync(person.Account.TenantId, chatId, cancellationToken);

        _botRegistry.RegisterChat(chatId, bot);

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
            switch (await _dictation.ReadAsync(botClient, voice, person.Account.TenantId, cancellationToken))
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
                // router still knows the person by.
                Principal = new Principal(ChannelIds.Telegram, sender, person.Account),
                // What the agent believes about who this is: the accounts service's word from the
                // answer that admitted the sender (TelegramAccess keeps it no longer than LinkCache).
                Assertion = person.Assertion,
                Attachments = intake.Attachments.Count > 0 ? intake.Attachments : null,
                ReviewCopy = ReviewOf(first.Chat, person),
                Timestamp = DateTimeOffset.UtcNow
            },
            cancellationToken);

        if (!live)
        {
            _logger.LogWarning(
                "No live channel_receive subscriber; buffering message from {Sender} for later delivery", sender);
        }

        _logger.LogDebug("Emitted message notification for conversation {ConversationId} from {Sender} (bot: {Bot}, agent: {AgentId})",
            conversationId, sender, bot.Key, agentId);
    }

    // Every turn asks, so a person's Telegram conversations sit among their topics in the chat
    // client. The bot names the chat and says whether it is a group's; whose the copy is, and where
    // it goes, is the review host's to decide (ADR 0046).
    private static ReviewCopy ReviewOf(Chat chat, ResolvedAccount person) =>
        chat.Type == ChatType.Private
            ? new ReviewCopy { Title = $"Telegram: {person.DisplayName}" }
            : new ReviewCopy { Title = $"Telegram: {chat.Title ?? "group"}", Group = true };

    // Every refusal for one message in a single reply quoting the item that failed and naming its
    // file, the way the gate answers a stranger. Answers whether the turn stops:
    // the two grounds the intake found are properties of one file and drop only that file, while a
    // model that cannot read what was attached would answer as though nothing had been sent, and
    // an answer that silently ignores the question is worse than no answer.
    private async Task<bool> ReportRefusalsAsync(
        string? agentId,
        ITelegramBotClient botClient,
        Message first,
        Intake intake,
        CancellationToken cancellationToken)
    {
        // The same resolution WebChat asks, from the same catalogue shape, so the two channels
        // cannot disagree about which model is refusing. Permissive wherever the catalogue is
        // silent — a cold start, or a blip at the provider, must not remove the feature. Telegram
        // has no per-message model override, so the model a turn runs on is the agent's default; a
        // shared bot's chat that picked no agent names none, and is as silent.
        var capabilityRefusal = intake.Attachments.Count > 0
            ? AttachmentCapability.Refusal(
                agentId is null ? null : _agentCatalog.Get(agentId), null, intake.Attachments.Select(a => a.MediaType))
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

    private sealed record Poller(TelegramBot Bot, CancellationTokenSource Stop, Task Polling);
}