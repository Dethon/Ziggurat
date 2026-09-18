using System.Text.Json;
using Agent.Settings;
using Domain.DTOs;
using Domain.Memory;
using Infrastructure.Judgments;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Integration.Memory;

// The probe's three labelled sets as a test, run through the real question builders against live
// Jev with the shipped bars. Synthetic data only — no production conversation is ever added to
// it. What it pins is what the feature is set for: the gate never skips a turn that holds a
// memory, the check keeps every keeper and drops every junk candidate, and the pair relation
// links right except the recorded conservative misses, which may only ever fail toward "not
// linked": the Laura pair the probe missed, and the light-novels pair the probe answered "same"
// at 0.32 — a coin flip under either wording, measured again on 2026-09-18, and a miss that
// leaves both memories in place. Like Category=Llm it runs whenever a key is present, because a
// pass costs cents.
[Trait("Category", "Jev")]
public class MemoryJudgeJevTests
{
    // Measured on 2026-09-18 against jev-1.13.0: 10 of 12 empty windows skipped. A floor below
    // that, so a single flip does not redden a run, and above what a question edit could lose.
    private const double GateSkipFloor = 0.65;

    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddUserSecrets<MemoryJudgeJevTests>()
        .AddEnvironmentVariables()
        .Build();

    private static readonly Lazy<Task<(MemoryJudge Judge, Cases Cases)>> Setup = new(SetupAsync);

    private sealed record Turn(string Role, string Text);

    private sealed record GateCase(IReadOnlyList<Turn> Context, string Current, bool HoldsSomething);

    private sealed record VerifyCase(IReadOnlyList<Turn> Context, string Current, string Candidate, bool Keep, string? Category);

    private sealed record PairCase(string A, string B, string Relation, bool MayFailTowardNotLinked);

    private sealed record Cases(IReadOnlyList<GateCase> Gate, IReadOnlyList<VerifyCase> Verify, IReadOnlyList<PairCase> Pairs);

    private static readonly MemoryJudgmentContext Context = new("jev-test");

    [SkippableFact]
    public async Task Gate_NeverSkipsATurnThatHoldsAMemory_AndSkipsMostThatDoNot()
    {
        var (judge, cases) = await Setup.Value;

        var verdicts = await RunAsync(cases.Gate, async c => (Case: c, Verdict: await judge.GateAsync(Window(c.Context, c.Current), Context, CancellationToken.None)));

        var falseSkips = verdicts
            .Where(v => v.Case.HoldsSomething && v.Verdict.Skip)
            .Select(v => $"'{v.Case.Current}' {Format(v.Verdict.Scores)}")
            .ToList();
        falseSkips.ShouldBeEmpty();

        var empty = verdicts.Where(v => !v.Case.HoldsSomething).ToList();
        var skipped = empty.Count(v => v.Verdict.Skip);
        ((double)skipped / empty.Count).ShouldBeGreaterThanOrEqualTo(
            GateSkipFloor, $"{skipped}/{empty.Count} empty windows skipped. Passed through:\n" +
                           string.Join("\n", empty.Where(v => !v.Verdict.Skip).Select(v => $"'{v.Case.Current}' {Format(v.Verdict.Scores)}")));
    }

    [SkippableFact]
    public async Task Check_KeepsEveryKeeper_AndDropsEveryJunkCandidate()
    {
        var (judge, cases) = await Setup.Value;

        var verdicts = await RunAsync(cases.Verify, async c =>
        {
            var category = c.Category is { } name ? Enum.Parse<MemoryCategory>(name) : MemoryCategory.Fact;
            var candidate = new ExtractionCandidate(c.Candidate, category, 0.5, 0.9, [], null);
            return (Case: c, Verdict: await judge.VerifyAsync(Window(c.Context, c.Current), candidate, Context, CancellationToken.None));
        });

        var wrong = verdicts
            .Where(v => v.Verdict.Store != v.Case.Keep)
            .Select(v => $"{(v.Case.Keep ? "dropped a keeper" : "kept junk")}: '{v.Case.Candidate}' {Format(v.Verdict.Scores)}")
            .ToList();
        wrong.ShouldBeEmpty();
        verdicts.Count(v => v.Case.Category == nameof(MemoryCategory.Instruction)).ShouldBe(2);
    }

    [SkippableFact]
    public async Task Pairs_LinkRight_ExceptTheRecordedMissWhichOnlyFailsTowardNotLinked()
    {
        var (judge, cases) = await Setup.Value;

        var verdicts = await RunAsync(cases.Pairs, async c =>
        {
            var pair = new[] { Memory("a", c.A), Memory("b", c.B) };
            var verdict = await judge.RelateAsync(pair, Context, CancellationToken.None);
            verdict.Answered.ShouldBeTrue($"Jev did not answer for '{c.A}' | '{c.B}'");
            return (Case: c, Linked: verdict.Linked.Count == 1);
        });

        var wrong = verdicts
            .Where(v => v.Linked != PairRelations.IsLink(v.Case.Relation))
            .Where(v => !(v.Case.MayFailTowardNotLinked && !v.Linked))
            .Select(v => $"'{v.Case.A}' | '{v.Case.B}' wanted {v.Case.Relation}, got {(v.Linked ? "linked" : "not linked")}")
            .ToList();
        wrong.ShouldBeEmpty();
    }

    private static async Task<(MemoryJudge, Cases)> SetupAsync()
    {
        var apiKey = Configuration["typeSafe:apiKey"] ?? Configuration["TYPESAFE_API_KEY"];
        Skip.If(string.IsNullOrWhiteSpace(apiKey), "typeSafe:apiKey is not set in user secrets (nor TYPESAFE_API_KEY)");

        var shipped = new ConfigurationBuilder()
                          .AddJsonFile(Path.Combine(RepositoryRoot.Path, "Agent", "appsettings.json"))
                          .Build();
        var typeSafe = shipped.Get<AgentSettings>()?.TypeSafe ?? throw new InvalidOperationException("appsettings.json did not bind");
        var settings = shipped.GetSection("Memory:Judgments").Get<MemoryJudgmentSettings>()
                       ?? throw new InvalidOperationException("Memory:Judgments did not bind");
        settings.Enabled.ShouldBeTrue("the shipped configuration has the judgments on");

        var judge = TypeSafeJudge.Create(
            new HttpClient(),
            new TypeSafeOptions { ApiUrl = typeSafe.ApiUrl, ApiKey = apiKey!, Model = typeSafe.Model },
            NullLogger.Instance);

        var cases = JsonSerializer.Deserialize<Cases>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Integration", "Memory", "jev-memory-cases.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("no cases");
        cases.Gate.Count.ShouldBeGreaterThan(20);
        cases.Verify.Count.ShouldBeGreaterThan(20);
        cases.Pairs.Count.ShouldBeGreaterThan(10);

        return (new MemoryJudge(judge, settings, TimeProvider.System), cases);
    }

    // A few at a time: the sets are small and the service is shared.
    private static async Task<IReadOnlyList<T>> RunAsync<TCase, T>(IReadOnlyList<TCase> cases, Func<TCase, Task<T>> run)
    {
        using var width = new SemaphoreSlim(4);
        return await Task.WhenAll(cases.Select(async c =>
        {
            await width.WaitAsync();
            try
            {
                return await run(c);
            }
            finally
            {
                width.Release();
            }
        }));
    }

    private static IReadOnlyList<ChatMessage> Window(IReadOnlyList<Turn> context, string current) =>
        [.. context.Select(t => new ChatMessage(t.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, t.Text)), new ChatMessage(ChatRole.User, current)];

    private static MemoryEntry Memory(string id, string content) => new()
    {
        Id = id, UserId = "jev-test", Category = MemoryCategory.Fact, Content = content,
        Importance = 0.5, Confidence = 0.9, CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
    };

    private static string Format(IReadOnlyDictionary<string, double> scores) =>
        "{" + string.Join(", ", scores.Select(s => $"{s.Key}: {s.Value:F2}")) + "}";
}