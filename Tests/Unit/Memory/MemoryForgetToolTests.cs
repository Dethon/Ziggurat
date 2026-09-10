using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.Memory;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryForgetToolTests
{
    private const string UserId = "user1";

    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IEmbeddingService> _embedding = new();

    private MemoryForgetTool CreateTool(string? userId = UserId) =>
        new(_store.Object, _embedding.Object, new FeatureConfig(UserId: userId));

    private float[] StubEmbedding(string query, params float[] values)
    {
        _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(values);
        return values;
    }

    private void StubDelete(params string[] memoryIds)
    {
        foreach (var id in memoryIds)
        {
            _store.Setup(s => s.DeleteAsync(UserId, id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        }
    }

    [Fact]
    public async Task Run_WithQuery_GeneratesEmbeddingAndUsesSearchAsync()
    {
        var query = "my job";
        var fakeEmbedding = StubEmbedding(query, 0.1f, 0.2f, 0.3f);
        var memory = CreateMemory("mem1", "I work at Acme Corp", MemoryCategory.Fact);

        _store.Setup(s => s.SearchAsync(
                UserId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MemorySearchResult(memory, 0.9)]);
        StubDelete("mem1");

        var result = await CreateTool().Run(query: query);

        _embedding.Verify(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.SearchAsync(
            UserId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()), Times.Once);
        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Run_WithMemoryId_StillUsesDirectLookup()
    {
        var memory = CreateMemory("mem1", "Direct lookup", MemoryCategory.Fact);

        _store.Setup(s => s.GetByIdAsync(UserId, "mem1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(memory);
        StubDelete("mem1");

        var result = await CreateTool().Run(memoryId: "mem1");

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        _embedding.Verify(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.SearchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]?>(),
            It.IsAny<IEnumerable<MemoryCategory>?>(), It.IsAny<IEnumerable<string>?>(),
            It.IsAny<double?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_QueryMatchingSeveral_DeletesNothingAndListsTheCandidates()
    {
        // The store's vector search is k-nearest with no relevance floor: "nearest" is not
        // "related", so a query that reaches more than one memory is a question, not an order.
        var query = "el piso";
        var fakeEmbedding = StubEmbedding(query, 0.1f);
        var flat = CreateMemory("mem1", "Busca piso de alquiler en Chamberí", MemoryCategory.Project);
        var coffee = CreateMemory("mem2", "Le gusta el café solo", MemoryCategory.Preference);

        _store.Setup(s => s.SearchAsync(
                UserId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MemorySearchResult(flat, 0.9),
                new MemorySearchResult(coffee, 0.4)
            ]);

        var result = await CreateTool().Run(query: query);

        result["status"]!.GetValue<string>().ShouldBe("confirmation_required");
        result["affectedCount"]!.GetValue<int>().ShouldBe(0);
        var candidates = result["candidates"]!.AsArray();
        candidates.Count.ShouldBe(2);
        candidates[0]!["id"]!.GetValue<string>().ShouldBe("mem1");
        candidates[0]!["content"]!.GetValue<string>().ShouldContain("Chamberí");
        candidates[1]!["id"]!.GetValue<string>().ShouldBe("mem2");
        result["message"]!.GetValue<string>().ShouldContain("memoryIds");
        _store.Verify(s => s.DeleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_WithMemoryIds_DeletesExactlyThose()
    {
        var flat = CreateMemory("mem1", "Busca piso de alquiler en Chamberí", MemoryCategory.Project);
        var move = CreateMemory("mem3", "Se muda en septiembre", MemoryCategory.Event);

        _store.Setup(s => s.GetByIdAsync(UserId, "mem1", It.IsAny<CancellationToken>())).ReturnsAsync(flat);
        _store.Setup(s => s.GetByIdAsync(UserId, "mem3", It.IsAny<CancellationToken>())).ReturnsAsync(move);
        StubDelete("mem1", "mem3");

        var result = await CreateTool().Run(memoryIds: ["mem1", "mem3"]);

        result["affectedCount"]!.GetValue<int>().ShouldBe(2);
        _store.Verify(s => s.DeleteAsync(UserId, "mem1", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.DeleteAsync(UserId, "mem3", It.IsAny<CancellationToken>()), Times.Once);
        _embedding.Verify(e => e.GenerateEmbeddingAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_NoMemoryIdOrQuery_ReturnsError()
    {
        var result = await CreateTool().Run();

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldBe("Either memoryId, memoryIds or query must be provided");
    }

    // An id nothing has is a guess, and answering it with "success, nothing affected" let the
    // guess look like a deletion. Refused, naming where the real ids are.
    [Fact]
    public async Task Run_AnUnknownMemoryId_IsRefused_NotAnEmptySuccess()
    {
        _store.Setup(s => s.GetByIdAsync(UserId, "mem_acme", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry?)null);

        var result = await CreateTool().Run(memoryId: "mem_acme");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
        result["hint"]!.GetValue<string>().ShouldContain("[Memory context]");
        _store.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The plural form is the same guess with a list around it: an id nothing has, answered with
    // "success, nothing affected", reads as a deletion that happened.
    [Fact]
    public async Task Run_MemoryIdsNamingNothing_IsRefused_NotAnEmptySuccess()
    {
        _store.Setup(s => s.GetByIdAsync(UserId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry?)null);

        var result = await CreateTool().Run(memoryIds: ["mem_acme", "mem_ghost"]);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
        _store.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A list where only some ids exist is not a refusal: the ones that were there are forgotten,
    // and the answer names the ones that were not, so the model does not read a partial pass as a
    // whole one.
    [Fact]
    public async Task Run_MemoryIdsPartlyUnknown_ForgetsWhatExists_AndNamesTheRest()
    {
        var flat = CreateMemory("mem1", "Busca piso de alquiler en Chamberí", MemoryCategory.Project);
        _store.Setup(s => s.GetByIdAsync(UserId, "mem1", It.IsAny<CancellationToken>())).ReturnsAsync(flat);
        _store.Setup(s => s.GetByIdAsync(UserId, "mem_ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry?)null);
        StubDelete("mem1");

        var result = await CreateTool().Run(memoryIds: ["mem1", "mem_ghost"]);

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        result["unknownIds"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["mem_ghost"]);
    }

    // The ids are written bracketed in the memory block, so a model copying one as it reads it
    // passes the brackets too. Answering that with "no memory has the id '[mem1]'" and a hint
    // saying "exactly as written there" is a loop; the brackets are stripped instead.
    [Fact]
    public async Task Run_ABracketedMemoryId_IsReadAsTheIdInside()
    {
        var flat = CreateMemory("mem1", "Busca piso de alquiler en Chamberí", MemoryCategory.Project);
        _store.Setup(s => s.GetByIdAsync(UserId, "mem1", It.IsAny<CancellationToken>())).ReturnsAsync(flat);
        StubDelete("mem1");

        var result = await CreateTool().Run(memoryId: "[mem1]");

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        _store.Verify(s => s.DeleteAsync(UserId, "mem1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Memory is scoped per user, so a run with no identity has nothing to scope to. That is a
    // missing credential, not a passing outage — the envelope says so and offers no retry.
    public async Task Run_MissingUserIdInFeatureConfig_RefusesAsAuthenticationAndDoesNotTouchStore(string? userId)
    {
        var result = await CreateTool(userId).Run(memoryId: "mem1");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.Authentication);
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
        result["hint"]!.ShouldNotBeNull();
        _store.Verify(s => s.GetByIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.DeleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.SearchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]?>(),
            It.IsAny<IEnumerable<MemoryCategory>?>(), It.IsAny<IEnumerable<string>?>(),
            It.IsAny<double?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static MemoryEntry CreateMemory(
        string id, string content, MemoryCategory category,
        double importance = 0.5, DateTimeOffset? createdAt = null) =>
        new()
        {
            Id = id,
            UserId = UserId,
            Category = category,
            Content = content,
            Importance = importance,
            Confidence = 0.8,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            Tags = []
        };
}