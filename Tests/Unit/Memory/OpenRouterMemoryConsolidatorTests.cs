using Domain.Contracts;
using Domain.DTOs;
using Domain.Judgments;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.Judgments;

namespace Tests.Unit.Memory;

public class OpenRouterMemoryConsolidatorTests
{
    private readonly Mock<IChatClient> _chatClient = new();
    private readonly OpenRouterMemoryConsolidator _consolidator;

    public OpenRouterMemoryConsolidatorTests()
    {
        // The pair judgment answers absence throughout the older tests: the consolidator's calls
        // and decisions are then exactly what they were before it existed.
        _consolidator = Consolidator(StubJudge.Absent(AbsenceReason.Unconfigured));
    }

    private OpenRouterMemoryConsolidator Consolidator(
        IJudge judge, MemoryJudgmentSettings? settings = null, bool enabled = true) =>
        new(
            _chatClient.Object,
            new MemoryJudge(judge, settings ?? new MemoryJudgmentSettings { Enabled = enabled }, new FakeTimeProvider()),
            Mock.Of<ILogger<OpenRouterMemoryConsolidator>>());

    private static JudgmentOutcome Relations(params (string Pair, string Relation)[] answers) =>
        new JudgmentOutcome.Answered(new Judgment(
            "jev-test",
            answers.ToDictionary(
                a => a.Pair,
                a => (JudgmentAnswer)new ChoiceAnswer(a.Relation, 0.9, new Dictionary<string, double>()),
                StringComparer.Ordinal),
            new JudgmentUsage(500, 0)));

    private List<string> CapturingPrompts()
    {
        var prompts = new List<string>();
        _chatClient.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((msgs, _, _) =>
                prompts.Add(string.Join("\n", msgs.Select(m => m.Text))))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"decisions": []}""")));
        return prompts;
    }

    // Cosine puts the four together; the pair judgment links only Madrid and Valencia.
    private static MemoryEntry[] TheMoveAndTheSiblings() =>
    [
        CreateMemory("mem_madrid", "Vive en Madrid", embedding: [1.0f, 0.0f, 0.0f]),
        CreateMemory("mem_valencia", "Se ha mudado a Valencia", embedding: [0.99f, 0.01f, 0.0f]),
        CreateMemory("mem_sister", "Su hermana Laura vive en Sevilla", embedding: [0.98f, 0.02f, 0.0f]),
        CreateMemory("mem_brother", "Su hermano Pablo vive en Bilbao", embedding: [0.97f, 0.03f, 0.0f])
    ];

    [Fact]
    public async Task ConsolidateAsync_OnlyMemoriesThePairJudgmentLinks_ReachTheMergeModelTogether()
    {
        var prompts = CapturingPrompts();
        var consolidator = Consolidator(StubJudge.Answering(Relations(
            ("0-1", "updates"), ("0-2", "unrelated"), ("0-3", "unrelated"),
            ("1-2", "unrelated"), ("1-3", "unrelated"), ("2-3", "distinct"))));

        var consolidation = await consolidator.ConsolidateAsync(TheMoveAndTheSiblings(), CancellationToken.None);

        var prompt = prompts.ShouldHaveSingleItem();
        prompt.ShouldContain("mem_madrid");
        prompt.ShouldContain("mem_valencia");
        prompt.ShouldNotContain("mem_sister");
        prompt.ShouldNotContain("mem_brother");
        consolidation.MergeableGroups.ShouldHaveSingleItem().ShouldBe(["mem_madrid", "mem_valencia"], ignoreOrder: true);
    }

    [Fact]
    public async Task ConsolidateAsync_WhenThePairJudgmentIsAbsent_TheClusterGoesAsCosineMadeIt()
    {
        var prompts = CapturingPrompts();
        var consolidator = Consolidator(StubJudge.Absent(AbsenceReason.Deadline));

        var consolidation = await consolidator.ConsolidateAsync(TheMoveAndTheSiblings(), CancellationToken.None);

        var prompt = prompts.ShouldHaveSingleItem();
        prompt.ShouldContain("mem_madrid");
        prompt.ShouldContain("mem_brother");
        consolidation.MergeableGroups.ShouldHaveSingleItem().Count.ShouldBe(4);
    }

    // A cluster over the cap is judged in chunks of the cap, nearest the centroid first, all in
    // the same pass: every member is judged, no request carries more than 66 pairs, and links
    // are only ever within a chunk.
    [Fact]
    public async Task ConsolidateAsync_AClusterOverTheCap_IsJudgedInCentroidOrderedChunks_AllInOnePass()
    {
        var prompts = CapturingPrompts();
        var judge = new StubJudge(request => Relations(request.Questions.Keys.Select(k => (k, "same")).ToArray()));
        var consolidator = Consolidator(judge);

        // Twelve sit on the axis, eighteen a little off it on either side: one cosine cluster
        // whose centroid is the axis, so the twelve are the nearest and go first.
        var near = Enumerable.Range(0, 12).Select(i => CreateMemory($"near_{i}", $"near {i}", embedding: [1.0f, 0.0f]));
        var far = Enumerable.Range(0, 18).Select(i => CreateMemory($"far_{i}", $"far {i}", embedding: [0.9f, i % 2 == 0 ? 0.2f : -0.2f]));

        var consolidation = await consolidator.ConsolidateAsync([.. near, .. far], CancellationToken.None);

        judge.Requests.Select(r => r.State["memories"]!.AsArray().Count).ShouldBe([12, 12, 6]);
        judge.Requests[0].Questions.Count.ShouldBe(66);
        judge.Requests[0].State["memories"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldAllBe(text => text.StartsWith("near"));

        // Three chunks, each linked whole by the stub, so three merge-model calls and three groups.
        prompts.Count.ShouldBe(3);
        Enumerable.Range(0, 12).ShouldAllBe(i => prompts[0].Contains($"[near_{i}]"));
        prompts.SelectMany(p => Enumerable.Range(0, 18).Where(i => p.Contains($"[far_{i}]"))).Count().ShouldBe(18);
        consolidation.MergeableGroups.Select(g => g.Count).ShouldBe([12, 12, 6]);
    }

    // A trailing chunk of one has nothing to merge against — the same reason BuildClusters drops
    // singleton clusters — and RelateAsync answers Unanswered below two, so it used to reach the
    // merge model alone: a paid call that can decide nothing.
    [Fact]
    public async Task ConsolidateAsync_AClusterOneOverTheCap_NeverSendsTheOddMemoryToTheMergeModelAlone()
    {
        var prompts = CapturingPrompts();
        var judge = new StubJudge(request => Relations(request.Questions.Keys.Select(k => (k, "same")).ToArray()));
        var consolidator = Consolidator(judge);

        var memories = Enumerable.Range(0, 13)
            .Select(i => CreateMemory($"m_{i}", $"m {i}", embedding: [1.0f, i * 0.001f]))
            .ToArray();

        var consolidation = await consolidator.ConsolidateAsync(memories, CancellationToken.None);

        judge.Requests.ShouldAllBe(r => r.State["memories"]!.AsArray().Count >= 2);
        prompts.ShouldAllBe(p => p.Count(c => c == '\n') >= 1);
        consolidation.MergeableGroups.ShouldAllBe(g => g.Count >= 2);
    }

    // The cap bounds a Jev request, so with no Jev there is nothing to bound: chunking a cluster
    // the judge will never see only makes cross-chunk duplicates unmergeable, which is strictly
    // worse than the behaviour it is supposed to fail toward.
    [Fact]
    public async Task ConsolidateAsync_WithTheJudgeDisabled_AnOversizedClusterGoesWholeToTheMergeModel()
    {
        var prompts = CapturingPrompts();
        var consolidator = Consolidator(StubJudge.Absent(AbsenceReason.Unconfigured), enabled: false);

        var memories = Enumerable.Range(0, 30)
            .Select(i => CreateMemory($"m_{i}", $"m {i}", embedding: [1.0f, i * 0.001f]))
            .ToArray();

        var consolidation = await consolidator.ConsolidateAsync(memories, CancellationToken.None);

        prompts.ShouldHaveSingleItem();
        consolidation.MergeableGroups.ShouldHaveSingleItem().Count.ShouldBe(30);
    }

    [Fact]
    public async Task ConsolidateAsync_AClusterOverTheCap_WithTheJudgeAbsent_GoesChunkByChunkAsCosineMadeIt()
    {
        var prompts = CapturingPrompts();
        var consolidator = Consolidator(StubJudge.Absent(AbsenceReason.Deadline));

        var memories = Enumerable.Range(0, 15).Select(i => CreateMemory($"m_{i}", $"m {i}", embedding: [1.0f, i * 0.01f])).ToArray();

        var consolidation = await consolidator.ConsolidateAsync(memories, CancellationToken.None);

        prompts.Count.ShouldBe(2);
        consolidation.MergeableGroups.Select(g => g.Count).ShouldBe([12, 3]);
    }

    [Fact]
    public async Task ConsolidateAsync_ThePairQuestionsReferenceTheStateByIndex_AndCarryNoMemoryId()
    {
        CapturingPrompts();
        var judge = StubJudge.Absent(AbsenceReason.Error);
        var consolidator = Consolidator(judge);

        await consolidator.ConsolidateAsync(TheMoveAndTheSiblings(), CancellationToken.None);

        var request = judge.Requests.ShouldHaveSingleItem();
        request.State.ToJsonString().ShouldNotContain("mem_");
        request.Questions.Keys.ShouldBe(["0-1", "0-2", "0-3", "1-2", "1-3", "2-3"], ignoreOrder: true);
        var question = request.Questions["1-2"].ShouldBeOfType<ChoiceQuestion>();
        question.Instructions.ShouldContain("`memories[1]` and `memories[2]`");
        question.Instructions.ShouldNotContain("mem_");
        question.Criteria.Keys.ShouldBe(["same", "updates", "distinct", "unrelated"]);
    }

    [Fact]
    public async Task ConsolidateAsync_WithMergeDecision_ReturnsMergeAction()
    {
        var mergeJson = """
            {"decisions": [{"sourceIds": ["mem_1", "mem_2"], "action": "merge", "mergedContent": "Works at Contoso on .NET projects", "category": "fact", "importance": 0.85, "tags": ["work"]}]}
            """;

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, mergeJson)));

        var memories = new[]
        {
            CreateMemory("mem_1", "Works at Contoso"),
            CreateMemory("mem_2", "Works on .NET projects")
        };

        var result = (await _consolidator.ConsolidateAsync(memories, CancellationToken.None)).Decisions;

        result.Count.ShouldBe(1);
        result[0].Action.ShouldBe(MergeAction.Merge);
        result[0].SourceIds.ShouldBe(new[] { "mem_1", "mem_2" });
        result[0].MergedContent.ShouldBe("Works at Contoso on .NET projects");
        result[0].Category.ShouldBe(MemoryCategory.Fact);
        result[0].Importance.ShouldBe(0.85);
        result[0].Tags.ShouldBe(new[] { "work" });
    }

    [Fact]
    public async Task ConsolidateAsync_WithEmptyResponse_ReturnsEmpty()
    {
        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"decisions": []}""")));

        var memories = new[] { CreateMemory("mem_1", "Some memory") };

        var result = await _consolidator.ConsolidateAsync(memories, CancellationToken.None);

        result.Decisions.ShouldBeEmpty();
    }

    [Fact]
    public async Task SynthesizeProfileAsync_ReturnsPersonalityProfile()
    {
        var profileJson = """
            {
              "summary": "A senior .NET developer at Contoso who prefers concise technical answers.",
              "communicationStyle": {
                "preference": "concise and technical",
                "avoidances": ["long explanations", "marketing speak"],
                "appreciated": ["code examples", "direct answers"]
              },
              "technicalContext": {
                "expertise": [".NET", "C#", "Azure"],
                "learning": ["Rust", "WebAssembly"]
              },
              "interactionGuidelines": ["Be direct", "Prefer code over prose"],
              "activeProjects": ["Agent AI assistant", "Idealista scraper"]
            }
            """;

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, profileJson)));

        var memories = Enumerable.Range(1, 5)
            .Select(i => CreateMemory($"mem_{i}", $"Memory content {i}"))
            .ToArray();

        var result = await _consolidator.SynthesizeProfileAsync("user1", memories, CancellationToken.None);

        result.UserId.ShouldBe("user1");
        result.Summary.ShouldBe("A senior .NET developer at Contoso who prefers concise technical answers.");
        result.BasedOnMemoryCount.ShouldBe(5);
        result.Confidence.ShouldBe(Math.Min(1.0, 5.0 / 20));

        result.CommunicationStyle.ShouldNotBeNull();
        result.CommunicationStyle!.Preference.ShouldBe("concise and technical");
        result.CommunicationStyle.Avoidances.ShouldBe(new[] { "long explanations", "marketing speak" });
        result.CommunicationStyle.Appreciated.ShouldBe(new[] { "code examples", "direct answers" });

        result.TechnicalContext.ShouldNotBeNull();
        result.TechnicalContext!.Expertise.ShouldBe(new[] { ".NET", "C#", "Azure" });
        result.TechnicalContext.Learning.ShouldBe(new[] { "Rust", "WebAssembly" });

        result.InteractionGuidelines.ShouldBe(new[] { "Be direct", "Prefer code over prose" });
        result.ActiveProjects.ShouldBe(new[] { "Agent AI assistant", "Idealista scraper" });
    }

    [Fact]
    public async Task ConsolidateAsync_WithNoEmbeddings_SendsSingleCall()
    {
        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"decisions": []}""")));

        var memories = new[]
        {
            CreateMemory("mem_1", "Works at Contoso"),
            CreateMemory("mem_2", "Works on .NET projects"),
            CreateMemory("mem_3", "Unrelated thing")
        };

        await _consolidator.ConsolidateAsync(memories, CancellationToken.None);

        _chatClient.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConsolidateAsync_WithEmbeddings_ClustersSimilarMemoriesIntoSeparateLlmCalls()
    {
        // Two clusters: cluster A (nearly identical vectors), cluster B (orthogonal)
        var clusterA1 = CreateMemory("a1", "Has a Japan Rail Pass", embedding: [1.0f, 0.0f, 0.0f]);
        var clusterA2 = CreateMemory("a2", "User has a JR Pass for the trip", embedding: [0.99f, 0.01f, 0.0f]);
        var clusterA3 = CreateMemory("a3", "Rail pass available", embedding: [0.98f, 0.02f, 0.0f]);
        var clusterB1 = CreateMemory("b1", "Watches Dragon Raja", embedding: [0.0f, 1.0f, 0.0f]);
        var clusterB2 = CreateMemory("b2", "Anime: Dragon Raja", embedding: [0.01f, 0.99f, 0.0f]);

        var capturedPrompts = new List<string>();
        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((msgs, _, _) =>
                capturedPrompts.Add(string.Join("\n", msgs.Select(m => m.Text))))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"decisions": []}""")));

        await _consolidator.ConsolidateAsync(
            [clusterA1, clusterA2, clusterA3, clusterB1, clusterB2],
            CancellationToken.None);

        // Two clusters → two LLM calls
        capturedPrompts.Count.ShouldBe(2);

        var promptA = capturedPrompts.Single(p => p.Contains("a1"));
        promptA.ShouldContain("a2");
        promptA.ShouldContain("a3");
        promptA.ShouldNotContain("b1");
        promptA.ShouldNotContain("b2");

        var promptB = capturedPrompts.Single(p => p.Contains("b1"));
        promptB.ShouldContain("b2");
        promptB.ShouldNotContain("a1");
    }

    [Fact]
    public async Task ConsolidateAsync_SkipsSingletonClusters()
    {
        // Singleton (no similar neighbor) has nothing to merge against → no LLM call needed.
        var loner = CreateMemory("x", "Unique thing", embedding: [1.0f, 0.0f, 0.0f]);
        var pair1 = CreateMemory("p1", "Thing A", embedding: [0.0f, 1.0f, 0.0f]);
        var pair2 = CreateMemory("p2", "Thing A restated", embedding: [0.01f, 0.99f, 0.0f]);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"decisions": []}""")));

        await _consolidator.ConsolidateAsync([loner, pair1, pair2], CancellationToken.None);

        _chatClient.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConsolidateAsync_AggregatesDecisionsFromAllClusters()
    {
        var a1 = CreateMemory("a1", "A first", embedding: [1.0f, 0.0f]);
        var a2 = CreateMemory("a2", "A second", embedding: [0.99f, 0.01f]);
        var b1 = CreateMemory("b1", "B first", embedding: [0.0f, 1.0f]);
        var b2 = CreateMemory("b2", "B second", embedding: [0.01f, 0.99f]);

        _chatClient.SetupSequence(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"decisions":[{"sourceIds":["a1","a2"],"action":"merge","mergedContent":"A merged","category":"fact","importance":0.8,"tags":[]}]}""")))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"decisions":[{"sourceIds":["b1","b2"],"action":"merge","mergedContent":"B merged","category":"fact","importance":0.8,"tags":[]}]}""")));

        var result = (await _consolidator.ConsolidateAsync([a1, a2, b1, b2], CancellationToken.None)).Decisions;

        result.Count.ShouldBe(2);
        result.Select(r => r.MergedContent).ShouldBe(new[] { "A merged", "B merged" }, ignoreOrder: true);
    }

    private static MemoryEntry CreateMemory(string id, string content, float[]? embedding = null) => new()
    {
        Id = id,
        UserId = "user1",
        Category = MemoryCategory.Fact,
        Content = content,
        Importance = 0.7,
        Confidence = 0.9,
        Embedding = embedding,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow
    };
}