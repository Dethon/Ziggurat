using System.Collections.Concurrent;
using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Accounts;
using Mcp.Hosting;
using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Tests.Unit.McpChannelTelegram;

// Drives real Telegram updates through the real polling service against a mocked bot client, with
// a real channel inbox and emitter and a fake clock. A test asserts on what a person or the agent
// would observe — the notification that reached the channel inbox, and the messages the bot sent
// back — never on the intake or the album buffer, which exist to keep the rules free of a clock
// rather than to be a surface.
internal sealed class TelegramPollingHarness : IDisposable
{
    public const string SubscriberId = ChannelProtocol.ChannelClientNamePrefix + "telegram";
    public const string AgentId = "jack";

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<SendMessageRequest> _sent = new();

    private readonly ConcurrentDictionary<int, Action> _beforeBatch = new();

    // Alice is Telegram user 1 and Bob user 2, both linked to accounts in the household, unless a
    // test links others.
    public const long AliceId = 1;
    public const long BobId = 2;

    // A group chat's id is negative; a supergroup's starts -100.
    public const long FamilyGroup = -100_200;

    public TelegramPollingHarness(params long[] linkedUserIds) : this(null, linkedUserIds)
    {
    }

    // Against a real accounts service, through the client the deployment uses, instead of the fake:
    // nobody is linked until a test links them there.
    public TelegramPollingHarness(AccountsResolverClient accountsService) : this(accountsService, [])
    {
    }

    private TelegramPollingHarness(AccountsResolverClient? accountsService, long[] linkedUserIds)
    {
        Settings = new ChannelSettings
        {
            Bots = [new AgentBotConfig { AgentId = AgentId, BotToken = "unused" }],
            Dictation = Dictation
        };

        var linked = linkedUserIds.Length > 0
            ? linkedUserIds.Select(id => (Id: id, Name: $"person-{id}"))
            : [(AliceId, "alice"), (BobId, "bob")];
        foreach (var (id, name) in linked)
        {
            Accounts.Link(id, name);
        }

        IAccountResolver resolver = accountsService is null ? Accounts : accountsService;
        ITelegramAccounts telegramAccounts = accountsService is null ? Accounts : accountsService;
        Access = new TelegramAccess(resolver, telegramAccounts, Settings.LinkCache, Time);
        Inbox = new ChannelInbox(Time);
        Emitter = new ChannelNotificationEmitter(Inbox, DeliveryPolicy.BufferAlways, SubscriberId);
        BotRegistry = new BotRegistry(new Dictionary<string, ITelegramBotClient>
        {
            [AgentId] = BotClient.Object
        });

        Service = new TelegramBotService(
            BotRegistry,
            Emitter,
            CallbackRouter,
            Catalog,
            new VoiceNoteDictation(
                Transcriber, Dictation, Metrics, new Mock<ILogger<VoiceNoteDictation>>().Object),
            new TelegramGate(Access, telegramAccounts, new Mock<ILogger<TelegramGate>>().Object),
            Time,
            new Mock<ILogger<TelegramBotService>>().Object);

        BotClient
            .Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback((IRequest<Message> request, CancellationToken _) => _sent.Enqueue((SendMessageRequest)request))
            .Returns(Task.FromResult(new Message
            {
                Id = 1,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = 1, Type = ChatType.Private }
            }));
    }

    public Mock<ITelegramBotClient> BotClient { get; } = new();
    public FakeTelegramAccounts Accounts { get; } = new();
    public TelegramAccess Access { get; }
    public FakeTranscriber Transcriber { get; } = new();
    public RecordingMetricsPublisher Metrics { get; } = new();
    public DictationSettings Dictation { get; } = new();
    public ChannelSettings Settings { get; }
    public FakeTimeProvider Time { get; } = new();
    public ApprovalCallbackRouter CallbackRouter { get; } = new();
    public MutableAgentCatalog Catalog { get; } = new();
    public ChannelInbox Inbox { get; }
    public ChannelNotificationEmitter Emitter { get; }
    public BotRegistry BotRegistry { get; }
    public TelegramBotService Service { get; }

    public IReadOnlyList<SendMessageRequest> Sent => [.. _sent];

    // One poll answers with these updates; the next cancels, which is how the pump stops.
    public void Enqueue(params Update[] updates) => EnqueueSequence((TimeSpan.Zero, updates));

    // Several polls, each preceded by the time that passed since the one before it — which is how
    // an album's items arrive, one as each file finishes uploading.
    public void EnqueueSequence(params (TimeSpan Elapsed, Update[] Updates)[] batches)
    {
        var callCount = 0;
        BotClient
            .Setup(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .Returns((GetUpdatesRequest _, CancellationToken ct) =>
            {
                var index = Interlocked.Increment(ref callCount) - 1;
                if (index < batches.Length)
                {
                    if (_beforeBatch.TryGetValue(index, out var before))
                    {
                        before();
                    }

                    Time.Advance(batches[index].Elapsed);
                    return Task.FromResult(batches[index].Updates);
                }

                _cts.Cancel();
                return Task.FromException<Update[]>(new OperationCanceledException(ct));
            });
    }

    // Something that happens elsewhere between two polls — the accounts service coming back, a
    // person linked on another bot — before the batch at that index is answered.
    public void OnBatch(int index, Action before) => _beforeBatch[index] = before;

    public async Task RunAsync()
    {
        // A backstop, not the mechanism: the mock cancels the moment it is asked for a batch the
        // test never queued, which is what normally ends this run and happens in milliseconds. One
        // second was close enough to real scheduling that a loaded machine reached it first, cutting
        // the pump off before it had processed the update and reading as "nothing was sent". The
        // cap only has to catch a pump that never gets there at all.
        _cts.CancelAfter(TimeSpan.FromSeconds(30));
        await Service.StartAsync(_cts.Token);

        // The pump awaits every update in a batch before it asks for the next one, and the mock
        // cancels the token when it is asked for a batch the test never queued — so cancellation is
        // precisely the moment everything enqueued has been processed. Sleeping a fixed 200ms
        // instead was a guess about how long that takes: true on an idle machine, and on a loaded
        // one the update had not reached the inbox yet and the assertion read it as empty. The
        // CancelAfter above still bounds a test whose pump never gets that far.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await Service.StopAsync(CancellationToken.None);
    }

    // Registering the subscriber is what a poll does; several tests need one to exist before the
    // updates arrive so the emit reports live.
    public Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync() =>
        Inbox.ReceiveAsync(SubscriberId, TimeSpan.Zero, CancellationToken.None);

    // Taps a button on the last keyboard the bot sent, once it has sent one: an approval's keyboard
    // goes out on a call the test started but cannot await, since that call waits for the tap.
    public async Task TapAsync(long fromUserId, string button)
    {
        await Eventually.Until(() => LastKeyboard is not null, "the approval keyboard to be sent");

        var data = LastKeyboard!.InlineKeyboard.First()
            .Single(b => b.CallbackData!.StartsWith(button + ":", StringComparison.Ordinal)).CallbackData;
        await CallbackRouter.HandleCallbackQueryAsync(BotClient.Object, new CallbackQuery
        {
            Id = "cb-1",
            Data = data,
            From = new User { Id = fromUserId, IsBot = false, FirstName = "Someone" }
        }, CancellationToken.None);
    }

    private InlineKeyboardMarkup? LastKeyboard =>
        Sent.Select(r => r.ReplyMarkup).OfType<InlineKeyboardMarkup>().LastOrDefault();

    // Releases whatever the album buffer is holding, then lets the release settle.
    public async Task QuietForAsync(TimeSpan span)
    {
        Time.Advance(span);
        await Task.Delay(50, CancellationToken.None);
    }

    public void Dispose()
    {
        Service.Dispose();
        _cts.Dispose();
    }

    public static Message TextMessage(
        string text, long chatId = 100, string? username = "alice", long userId = AliceId) => new()
        {
            Id = 10,
            Date = DateTime.UtcNow,
            Text = text,
            Chat = new Chat { Id = chatId, Type = ChatType.Private },
            From = new User { Id = userId, IsBot = false, FirstName = username ?? "Someone", Username = username }
        };

    public static Message GroupMessage(
        string text, long chatId = FamilyGroup, string? username = "alice", long userId = AliceId) => new()
        {
            Id = 10,
            Date = DateTime.UtcNow,
            Text = text,
            Chat = new Chat { Id = chatId, Type = ChatType.Supergroup, Title = "Family" },
            From = new User { Id = userId, IsBot = false, FirstName = username ?? "Someone", Username = username }
        };

    public static Message MediaMessage(
        int messageId = 10,
        string? caption = null,
        long chatId = 100,
        string username = "alice",
        int? threadId = null,
        string? mediaGroupId = null) => new()
        {
            Id = messageId,
            Date = DateTime.UtcNow,
            Caption = caption,
            MessageThreadId = threadId,
            MediaGroupId = mediaGroupId,
            Chat = new Chat { Id = chatId, Type = ChatType.Private },
            From = new User { Id = AliceId, IsBot = false, FirstName = username, Username = username }
        };

    public static PhotoSize[] Photo(string fileId = "photo-1", long? sizeBytes = 2048) =>
    [
        new() { FileId = fileId, FileUniqueId = "u-" + fileId, Width = 1280, Height = 720, FileSize = sizeBytes }
    ];

    public static Voice VoiceNote(
        string fileId = "voice-1",
        int durationSeconds = 2,
        string? mimeType = "audio/ogg",
        long? sizeBytes = 8054) => new()
        {
            FileId = fileId,
            FileUniqueId = "u-" + fileId,
            Duration = durationSeconds,
            MimeType = mimeType,
            FileSize = sizeBytes
        };

    // A real Ogg/Opus file (libopus, 48 kHz mono, VoIP), which is what Telegram sends. The decode
    // is real in these tests; only the transcriber is faked.
    public static byte[] OggOpusFixture { get; } = Fixture("voice-note.ogg");

    public static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Unit/McpChannelTelegram/Fixtures", name));

    public void GivenTelegramHolds(string fileId, byte[] bytes, string path)
    {
        BotClient
            .Setup(b => b.SendRequest(
                It.Is<GetFileRequest>(r => r.FileId == fileId), It.IsAny<CancellationToken>()))
            .Returns((GetFileRequest request, CancellationToken _) => Task.FromResult(new TGFile
            {
                FileId = request.FileId,
                FileUniqueId = "u-" + request.FileId,
                FilePath = path,
                FileSize = bytes.Length
            }));

        BotClient
            .Setup(b => b.DownloadFile(path, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((string _, Stream destination, CancellationToken ct) =>
                destination.WriteAsync(bytes, ct).AsTask());
    }

    public static Document Document(
        string fileId = "doc-1",
        string? fileName = "report.pdf",
        string? mimeType = "application/pdf",
        long? sizeBytes = 4096) => new()
        {
            FileId = fileId,
            FileUniqueId = "u-" + fileId,
            FileName = fileName,
            MimeType = mimeType,
            FileSize = sizeBytes
        };
}