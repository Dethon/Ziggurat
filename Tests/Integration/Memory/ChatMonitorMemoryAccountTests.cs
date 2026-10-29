using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Judgments;
using Domain.Memory;
using Domain.Monitor;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit.Domain;
using Tests.Unit.Judgments;

namespace Tests.Integration.Memory;

// Whose memory a turn reads and writes, through the monitor that builds the turn, the recall hook
// and the extraction worker the deployment runs, against the real store. The extractor is the one
// stand-in — it remembers what the person said, word for word — so what these pin is where a memory
// is filed and who can read it back, never what the model would make of the words.
[Trait("Category", "Integration")]
public class ChatMonitorMemoryAccountTests(MemorySearchFixture redisFixture)
    : IClassFixture<MemorySearchFixture>, IAsyncLifetime
{
    private const string Tenant = "home";

    private readonly RedisStackMemoryStore _store =
        new(redisFixture.Connection, TestEmbeddingOptions.At(MemorySearchFixture.VectorWidth));

    private readonly MemoryExtractionQueue _queue = new();
    private readonly VerbatimExtractor _extractor = new();
    private MemoryExtractionWorker? _worker;

    public async Task InitializeAsync()
    {
        _worker = new MemoryExtractionWorker(
            _queue, _extractor, Embedding, _store, Mock.Of<IThreadStateStore>(),
            Mock.Of<IMetricsPublisher>(), Mock.Of<IAgentDefinitionProvider>(),
            new MemoryJudge(StubJudge.Absent(AbsenceReason.Unconfigured), new MemoryJudgmentSettings(), TimeProvider.System),
            NullLogger<MemoryExtractionWorker>.Instance, new MemoryExtractionOptions());
        await _worker.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        _queue.Complete();
        await _worker!.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFactToldOnTelegram_IsRecalledOnTheChatClientByItsAccountAndNoOther()
    {
        var fran = NewAccount();
        var ana = NewAccount();

        await TurnAsync(PersonTurn("telegram", "fran", fran, "Soy alérgico a los cacahuetes"));
        await Eventually.Until(
            async () => await _store.HasAnyMemoriesAsync(fran.UserId),
            "the fact told on Telegram to be filed under the person's account");

        var onTheChatClient = await TurnAsync(PersonTurn("signalr", "Fran", fran, "¿Qué ceno hoy?"));
        // Ana goes by the very name Fran used on Telegram: a name is a label, so it reaches nothing.
        var byAnotherPerson = await TurnAsync(PersonTurn("signalr", "fran", ana, "¿Qué ceno hoy?"));

        onTheChatClient.GetMemoryContext().ShouldNotBeNull()
            .Memories.ShouldContain(m => m.Memory.Content == "Soy alérgico a los cacahuetes");
        byAnotherPerson.GetMemoryContext().ShouldBeNull();
    }

    // The voice guest is nobody, even where a name it speaks under has memories filed by it — an
    // operator may set a satellite's identity to a person's name, and the guest's user id is the
    // household's. Neither is an account, so neither is a key.
    [Fact]
    public async Task AGuestTurn_RecallsNothingAndStoresNothing()
    {
        var satellite = $"fran-{Guid.NewGuid():N}";
        await _store.StoreAsync(Memory(satellite, "Tiene un gato"));
        await _store.StoreAsync(Memory("household", "Tiene un gato"));

        var guest = await TurnAsync(new ChannelMessage
        {
            ConversationId = $"voice-{Guid.NewGuid():N}",
            Content = "Soy alérgico a los cacahuetes, ¿tengo un gato?",
            Sender = satellite,
            ChannelId = "voice",
            SatelliteId = "kitchen",
            Principal = new Principal("voice", "household")
        });

        await Eventually.Settle();
        guest.GetMemoryContext().ShouldBeNull();
        _extractor.Calls.ShouldBe(0);
        (await _store.GetByUserIdAsync(satellite)).ShouldHaveSingleItem();
    }

    // A schedule's fire acts for whoever wrote it, so it reads what they told the agent. Its words
    // are the prompt they left for later, not something they just said, and before accounts a fire
    // was only ever extracted into the scheduler's own bucket: it writes into nobody's memory.
    [Fact]
    public async Task AFire_RecallsAsTheAccountThatWroteItAndStoresNothing()
    {
        var fran = NewAccount();
        await _store.StoreAsync(Memory(fran.UserId, "Le gusta el café solo"));

        var fire = await TurnAsync(new ChannelMessage
        {
            ConversationId = $"sched-{Guid.NewGuid():N}",
            Content = "Soy alérgico a los cacahuetes; recuérdame el café de las once",
            Sender = "scheduler",
            ChannelId = "scheduling",
            UserId = fran.UserId,
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "s-1"),
            Principal = new Principal("signalr", fran.UserId, fran)
        });

        await Eventually.Settle();
        fire.GetMemoryContext().ShouldNotBeNull()
            .Memories.ShouldContain(m => m.Memory.Content == "Le gusta el café solo");
        _extractor.Calls.ShouldBe(0);
        (await _store.GetByUserIdAsync(fran.UserId)).ShouldHaveSingleItem();
    }

    private static Account NewAccount() => new(Tenant, Guid.NewGuid().ToString("N"));

    private static MemoryEntry Memory(string userId, string content) => new()
    {
        Id = $"mem_{Guid.NewGuid():N}",
        UserId = userId,
        Category = MemoryCategory.Fact,
        Content = content,
        Importance = 0.9,
        Confidence = 0.9,
        Embedding = OneDirection.Vector,
        Tags = [],
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow
    };

    private static ChannelMessage PersonTurn(string channelId, string sender, Account account, string content) => new()
    {
        ConversationId = $"{channelId}-{Guid.NewGuid():N}",
        Content = content,
        Sender = sender,
        ChannelId = channelId,
        Principal = new Principal(channelId, account.UserId, account)
    };

    // One turn through a monitor of its own, and the message the agent was handed for it.
    private async Task<ChatMessage> TurnAsync(ChannelMessage message)
    {
        var agent = MonitorTestMocks.CreateAgent();
        var monitor = new ChatMonitor(
            [MonitorTestMocks.CreateChannel(message.ChannelId, message)],
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            Mock.Of<IMetricsPublisher>(),
            new MemoryRecallHook(
                _store, Embedding, Mock.Of<IThreadStateStore>(), _queue, Mock.Of<IMetricsPublisher>(),
                Mock.Of<IAgentDefinitionProvider>(), NullLogger<MemoryRecallHook>.Instance,
                new MemoryRecallOptions()),
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        agent.ReceivedMessages.TryDequeue(out var received).ShouldBeTrue();
        return received!.ShouldHaveSingleItem();
    }

    // Every text embeds in one direction, so whatever is stored is as near as a memory can be to
    // whatever is asked: a turn that recalls nothing was refused by whose memory it is, never by
    // distance.
    private static IEmbeddingService Embedding { get; } = new OneDirection();

    private sealed class OneDirection : IEmbeddingService
    {
        public static readonly float[] Vector =
            [1.0f, .. Enumerable.Repeat(0f, MemorySearchFixture.VectorWidth - 1)];

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Vector);

        public Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Select(_ => Vector).ToArray());
    }

    private sealed class VerbatimExtractor : IMemoryExtractor
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
            IReadOnlyList<ChatMessage> contextWindow, string userId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<ExtractionCandidate>>(
            [
                new ExtractionCandidate(contextWindow[^1].Text, MemoryCategory.Fact, 0.9, 0.9, [], null)
            ]);
        }
    }
}