using System.Collections.Concurrent;
using Domain.Agents;
using Domain.Channels;
using Domain.DTOs.Channel;
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

    public TelegramPollingHarness(params string[] allowedUsernames)
    {
        Inbox = new ChannelInbox(Time);
        Emitter = new ChannelNotificationEmitter(Inbox, DeliveryPolicy.BufferAlways, SubscriberId);
        BotRegistry = new BotRegistry(new Dictionary<string, ITelegramBotClient>
        {
            [AgentId] = BotClient.Object
        });

        Service = new TelegramBotService(
            BotRegistry,
            new ChannelSettings
            {
                Bots = [new AgentBotConfig { AgentId = AgentId, BotToken = "unused" }],
                AllowedUsernames = allowedUsernames.Length > 0 ? allowedUsernames : ["alice", "bob"],
                Dictation = Dictation
            },
            Emitter,
            CallbackRouter,
            Catalog,
            new VoiceNoteDictation(
                Transcriber, Dictation, Metrics, new Mock<ILogger<VoiceNoteDictation>>().Object),
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
    public FakeTranscriber Transcriber { get; } = new();
    public RecordingMetricsPublisher Metrics { get; } = new();
    public DictationSettings Dictation { get; } = new();
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
                    Time.Advance(batches[index].Elapsed);
                    return Task.FromResult(batches[index].Updates);
                }

                _cts.Cancel();
                return Task.FromException<Update[]>(new OperationCanceledException(ct));
            });
    }

    public async Task RunAsync()
    {
        _cts.CancelAfter(TimeSpan.FromSeconds(1));
        await Service.StartAsync(_cts.Token);
        await Task.Delay(200, CancellationToken.None);
        await Service.StopAsync(CancellationToken.None);
    }

    // Registering the subscriber is what a poll does; several tests need one to exist before the
    // updates arrive so the emit reports live.
    public Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync() =>
        Inbox.ReceiveAsync(SubscriberId, TimeSpan.Zero, CancellationToken.None);

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

    public static Message TextMessage(string text, long chatId = 100, string username = "alice") => new()
    {
        Id = 10,
        Date = DateTime.UtcNow,
        Text = text,
        Chat = new Chat { Id = chatId, Type = ChatType.Private },
        From = new User { Id = 1, IsBot = false, FirstName = username, Username = username }
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
            From = new User { Id = 1, IsBot = false, FirstName = username, Username = username }
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