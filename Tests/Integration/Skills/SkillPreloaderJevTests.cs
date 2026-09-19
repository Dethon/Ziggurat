using System.Text.Json;
using Agent.Settings;
using Domain.Judgments;
using Domain.Prompts;
using Domain.Skills;
using Infrastructure.Judgments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Eval.Harness;
using Tests.Unit.Domain.Prompts;

namespace Tests.Integration.Skills;

// The probe's skill half as a test: the labelled set beside it as data, run through the real
// preloader and policy against live Jev over the seven shipped descriptions. What it pins is the
// bar the feature is set for — no wrong preload — and a coverage floor measured on the day it
// landed, so a Jev version bump or a description edit is checked in a minute for cents. Like
// Category=Llm it runs whenever a key is present, because a pass costs well under a cent.
[Trait("Category", "Jev")]
public class SkillPreloaderJevTests
{
    // Measured on 2026-09-18 against jev-1.13.0 with the shipped descriptions: 32 of 34 cases
    // fully covered. A floor below that, so a single flip does not redden a run, and above what
    // a description edit could quietly lose.
    private const double CoverageFloor = 0.85;

    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<SkillPreloaderJevTests>()
        .AddEnvironmentVariables()
        .Build();

    private static readonly Lazy<Task<IReadOnlyList<Verdict>>> _run = new(RunAsync);

    private sealed record Case(string Request, IReadOnlyList<string> Skills);

    private sealed record Verdict(Case Case, SkillPreload Preload)
    {
        public IReadOnlySet<string> Preloaded => Preload.Skills.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        public IEnumerable<string> Wrong => Preloaded.Except(Case.Skills, StringComparer.Ordinal);

        public bool Covered => Case.Skills.All(Preloaded.Contains) && !Wrong.Any();
    }

    [SkippableFact]
    public async Task OverTheLabelledSet_NothingIsPreloadedThatTheRequestDoesNotNeed()
    {
        var verdicts = await _run.Value;

        var wrong = verdicts
            .Where(v => v.Wrong.Any())
            .Select(v => $"'{v.Case.Request}' preloaded {string.Join(", ", v.Wrong)} " +
                         $"(choice {v.Preload.Judgment?.Choice} at {v.Preload.Judgment?.ChoiceConfidence:F2})")
            .ToList();

        wrong.ShouldBeEmpty();
    }

    [SkippableFact]
    public async Task OverTheLabelledSet_CoverageHoldsTheFloor()
    {
        var verdicts = await _run.Value;

        var covered = verdicts.Count(v => v.Covered);
        var missed = verdicts
            .Where(v => !v.Covered)
            .Select(v => $"'{v.Case.Request}' wanted [{string.Join(", ", v.Case.Skills)}], got " +
                         $"[{string.Join(", ", v.Preloaded)}] (choice {v.Preload.Judgment?.Choice} at " +
                         $"{v.Preload.Judgment?.ChoiceConfidence:F2}; outcome {v.Preload.Outcome})")
            .ToList();

        ((double)covered / verdicts.Count).ShouldBeGreaterThanOrEqualTo(
            CoverageFloor, $"{covered}/{verdicts.Count} covered. Missed:\n{string.Join("\n", missed)}");
    }

    [SkippableFact]
    public async Task TheTwoOverlapsTheProbeFound_NowPreloadTheRightSkill()
    {
        var verdicts = await _run.Value;

        verdicts.Single(v => v.Case.Request == "para la alarma").Preloaded.ShouldBe(["countdown-timers"]);
        verdicts.Single(v => v.Case.Request == "dime cuando termine la lavadora").Preloaded.ShouldBe(["home-watches"]);
    }

    private static async Task<IReadOnlyList<Verdict>> RunAsync()
    {
        var apiKey = _configuration["openRouter:apiKey"];
        Skip.If(string.IsNullOrWhiteSpace(apiKey), "openRouter:apiKey is not set in user secrets");

        var shipped = new ConfigurationBuilder()
                          .AddJsonFile(Path.Combine(RepositoryRoot.Path, "Agent", "appsettings.json"))
                          .Build()
                          .Get<AgentSettings>()
                      ?? throw new InvalidOperationException("appsettings.json did not bind");
        var judge = TypeSafeJudge.Create(
            new HttpClient(),
            new TypeSafeOptions { ApiUrl = shipped.TypeSafe.ApiUrl, ApiKey = apiKey!, Model = shipped.TypeSafe.Model },
            NullLogger.Instance);
        // The shipped bars and cap; a generous deadline, because what is measured here is the
        // answer and not this network's tail.
        var preloader = new SkillPreloader(judge, shipped.SkillPreload with { DeadlineMs = 15_000 }, TimeProvider.System);
        var skills = AgentPromptFixture.ServedSkills.Values.ToList();
        skills.Count.ShouldBe(PromptManifest.Skills.Count);

        var cases = JsonSerializer.Deserialize<List<Case>>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Integration", "Skills", "jev-skill-cases.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        cases.Count.ShouldBeGreaterThan(30);

        // A few at a time: the set is small and the service is shared.
        using var width = new SemaphoreSlim(4);
        var verdicts = await Task.WhenAll(cases.Select(async c =>
        {
            await width.WaitAsync();
            try
            {
                var preload = await preloader.PreloadAsync(new SkillPreloadRequest(c.Request, skills, []), CancellationToken.None);
                preload.Outcome.ShouldNotBeOneOf(SkillPreloadOutcome.Error, SkillPreloadOutcome.NotAsked, SkillPreloadOutcome.Deadline);
                return new Verdict(c, preload);
            }
            finally
            {
                width.Release();
            }
        }));

        return verdicts;
    }
}