using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools;
using Domain.Tools.Memory;
using Infrastructure.Memory;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

// The forget tool against the real store, where the search it performs is a k-nearest query with
// no relevance floor: against a small store it returns everything, and "nearest" is not "related".
// What these tests pin is that reaching several memories asks instead of deleting — the eval's
// fake store matches lexically, so its "the fact beside it survived" is not evidence about this.
public class MemoryForgetToolRedisTests(MemorySearchFixture redisFixture)
    : IClassFixture<MemorySearchFixture>
{
    private const int EmbeddingDimension = MemorySearchFixture.VectorWidth;

    private RedisStackMemoryStore CreateStore() =>
        new(redisFixture.Connection, TestEmbeddingOptions.At(EmbeddingDimension));

    private static float[] DirectionAt(int index)
    {
        var embedding = new float[EmbeddingDimension];
        embedding[index % EmbeddingDimension] = 1.0f;
        return embedding;
    }

    private static MemoryEntry CreateMemory(string userId, string content, int direction) => new()
    {
        Id = $"mem_{Guid.NewGuid():N}",
        UserId = userId,
        Category = MemoryCategory.Fact,
        Content = content,
        Importance = 0.6,
        Confidence = 0.8,
        Embedding = DirectionAt(direction),
        Tags = [],
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow
    };

    // The tool set is built once per agent, by the turn that opened the conversation, and its
    // FeatureConfig.UserId is that turn's sender; whose memory a call reaches is the turn in flight.
    private static FeatureConfig ATurnOf(string accountUserId, string? openedBy = null) =>
        ATurnAs(new Principal("signalr", accountUserId, new Account("home", accountUserId)), openedBy);

    private static FeatureConfig ATurnAs(Principal principal, string? openedBy = null)
    {
        var context = new ConversationContext(
            "jonas", "conv-1", principal.UserId, new ReplyTarget(principal.ChannelId, "conv-1"),
            Principal: principal);
        return new FeatureConfig(UserId: openedBy, ConversationContextProvider: () => context);
    }

    // A conversation one person opened and another continues — a voice satellite two people speak
    // to, a shared topic — keeps the first one's tool set; the second still reaches only their own.
    [Fact]
    public async Task ForgetById_OnAnotherAccountsTurn_ReachesNothingOfTheirs()
    {
        var store = CreateStore();
        var fran = Guid.NewGuid().ToString("N");
        var ana = Guid.NewGuid().ToString("N");
        var coffee = CreateMemory(fran, "Le gusta el café solo", direction: 1);
        await store.StoreAsync(coffee);

        var tool = new MemoryForgetTool(
            store, new FixedEmbedding(DirectionAt(1)), ATurnOf(ana, openedBy: fran));
        var result = await tool.Run(memoryId: coffee.Id);

        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
        (await store.GetByUserIdAsync(fran)).ShouldHaveSingleItem();
    }

    // The voice guest is nobody: its turn forgets nothing, whatever name it speaks under.
    [Fact]
    public async Task ForgetById_OnAGuestTurn_IsRefusedAndTouchesNothing()
    {
        var store = CreateStore();
        var household = CreateMemory("household", "Tiene un gato", direction: 1);
        await store.StoreAsync(household);

        var tool = new MemoryForgetTool(
            store, new FixedEmbedding(DirectionAt(1)),
            ATurnAs(new Principal("voice", "household"), openedBy: "household"));
        var result = await tool.Run(memoryId: household.Id);

        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.Authentication);
        (await store.GetByIdAsync("household", household.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ForgetByQuery_AgainstAFewMemories_DeletesNothingWithoutConfirmation()
    {
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var flat = CreateMemory(userId, "Busca piso de alquiler en Chamberí", direction: 0);
        var coffee = CreateMemory(userId, "Le gusta el café solo", direction: 1);
        var job = CreateMemory(userId, "Trabaja en Acme como ingeniero de datos", direction: 2);
        await store.StoreAsync(flat);
        await store.StoreAsync(coffee);
        await store.StoreAsync(job);

        // The query embeds right on top of the flat memory; the k-nearest search still returns
        // the other two, because nothing in the store bounds "near".
        var tool = new MemoryForgetTool(
            store, new FixedEmbedding(DirectionAt(0)), ATurnOf(userId));
        var result = await tool.Run(query: "lo del piso");

        result["status"]!.GetValue<string>().ShouldBe("confirmation_required");
        result["affectedCount"]!.GetValue<int>().ShouldBe(0);
        (await store.GetByUserIdAsync(userId)).Count.ShouldBe(3);

        // The candidates arrive nearest-first, so the follow-up can name the one that was meant.
        var candidates = result["candidates"]!.AsArray();
        candidates.Count.ShouldBe(3);
        candidates[0]!["id"]!.GetValue<string>().ShouldBe(flat.Id);
    }

    [Fact]
    public async Task ForgetByIds_AfterTheConfirmation_TakesOnlyWhatWasNamed()
    {
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var flat = CreateMemory(userId, "Busca piso de alquiler en Chamberí", direction: 0);
        var coffee = CreateMemory(userId, "Le gusta el café solo", direction: 1);
        await store.StoreAsync(flat);
        await store.StoreAsync(coffee);

        var tool = new MemoryForgetTool(
            store, new FixedEmbedding(DirectionAt(0)), ATurnOf(userId));
        var result = await tool.Run(memoryIds: [flat.Id]);

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        var remaining = await store.GetByUserIdAsync(userId);
        remaining.Count.ShouldBe(1);
        remaining[0].Content.ShouldBe("Le gusta el café solo");
    }

    [Fact]
    public async Task ForgetByQuery_ReachingExactlyOne_StillDeletesIt()
    {
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var flat = CreateMemory(userId, "Busca piso de alquiler en Chamberí", direction: 0);
        await store.StoreAsync(flat);

        var tool = new MemoryForgetTool(
            store, new FixedEmbedding(DirectionAt(0)), ATurnOf(userId));
        var result = await tool.Run(query: "lo del piso");

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        (await store.GetByUserIdAsync(userId)).ShouldBeEmpty();
    }

    private sealed class FixedEmbedding(float[] vector) : IEmbeddingService
    {
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(vector);

        public Task<float[][]> GenerateEmbeddingsAsync(
            IEnumerable<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Select(_ => vector).ToArray());
    }
}