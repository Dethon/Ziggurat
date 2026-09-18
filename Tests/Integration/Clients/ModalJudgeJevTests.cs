using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Judgments;
using McpServerWebSearch.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Clients;

// The probe as a test: the labelled walls from .scratch/jev-modal-dismissal/probe/README.md run
// through the real question builders and the real rule against live Jev, at the shipped bar. Every
// listed pick and every `none` is pinned exactly, because a wrong click here is a wrong action on
// somebody's page — the bar the feature is set for. Like Category=Llm it runs whenever a key is
// present; a pass costs well under a cent.
[Trait("Category", "Jev")]
public class ModalJudgeJevTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddUserSecrets<ModalJudgeJevTests>()
        .AddEnvironmentVariables()
        .Build();

    private static readonly Lazy<Task<IReadOnlyList<Verdict>>> Run = new(RunAsync);

    private sealed record Case(
        [property: JsonConverter(typeof(JsonStringEnumConverter))] ModalType Kind,
        IReadOnlyList<string> Controls,
        string? Pick);

    private sealed record Verdict(Case Case, ModalPick Pick)
    {
        public string? Picked => Pick.Index is { } i ? Case.Controls[i] : null;
    }

    [SkippableFact]
    public async Task OverTheLabelledWalls_EveryPickIsTheOneTheProbeFound()
    {
        var verdicts = await Run.Value;

        var wrong = verdicts
            .Where(v => v.Picked != v.Case.Pick)
            .Select(v => $"{v.Case.Kind} [{string.Join(", ", v.Case.Controls)}] wanted " +
                         $"{v.Case.Pick ?? "none"}, got {v.Picked ?? "none"} ({v.Pick.Status} at {v.Pick.Confidence:F2})")
            .ToList();

        wrong.ShouldBeEmpty();
    }

    // The two the one-question shape got wrong, named: the settings control beside an accept, and
    // the wall that offers only an accept beside a link to more information.
    [SkippableFact]
    public async Task ManagePreferencesBesideGotIt_ClicksGotIt_AndAnAcceptOnlyWall_StillCloses()
    {
        var verdicts = await Run.Value;

        verdicts.Single(v => v.Case.Controls.Contains("Manage preferences")).Picked.ShouldBe("Got it");
        verdicts.Single(v => v.Case.Controls.Contains("Aceptar y continuar")).Picked.ShouldBe("Aceptar y continuar");
    }

    // The key the Jev tests share, or a skip: user secrets first, the environment second.
    internal static string RequireKey()
    {
        var apiKey = Configuration["typeSafe:apiKey"] ?? Configuration["TYPESAFE_API_KEY"];
        Skip.If(string.IsNullOrWhiteSpace(apiKey), "typeSafe:apiKey is not set in user secrets (nor TYPESAFE_API_KEY)");
        return apiKey!;
    }

    private static async Task<IReadOnlyList<Verdict>> RunAsync()
    {
        var judge = ShippedJudge(RequireKey());

        var cases = JsonSerializer.Deserialize<List<Case>>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Integration", "Clients", "jev-modal-cases.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        cases.Count.ShouldBeGreaterThan(12);

        // A few at a time: the set is small and the service is shared.
        using var width = new SemaphoreSlim(4);
        return await Task.WhenAll(cases.Select(async c =>
        {
            await width.WaitAsync();
            try
            {
                var controls = c.Controls.Select((name, i) => new ModalControl(i, "button", name)).ToList();
                var pick = await judge.PickAsync(c.Kind, controls, CancellationToken.None);
                pick.Status.ShouldNotBeOneOf(ModalPickStatus.Absent, ModalPickStatus.NotAsked);
                return new Verdict(c, pick);
            }
            finally
            {
                width.Release();
            }
        }));
    }

    // The judge exactly as the browse server ships it — its pinned model, its bar and its cap —
    // with a generous deadline, because what is measured here is the answer and not this
    // network's tail.
    internal static ModalJudge ShippedJudge(string apiKey)
    {
        var shipped = new ConfigurationBuilder()
                          .AddJsonFile(Path.Combine(McpServerRegistrations.RepoRoot, "McpServerWebSearch", "appsettings.json"))
                          .Build()
                          .Get<McpSettings>()
                      ?? throw new InvalidOperationException("appsettings.json did not bind");
        var judge = TypeSafeJudge.Create(
            new HttpClient(),
            new TypeSafeOptions { ApiUrl = shipped.TypeSafe.ApiUrl, ApiKey = apiKey, Model = shipped.TypeSafe.Model },
            NullLogger.Instance);
        return new ModalJudge(judge, shipped.Judgment with { DeadlineMs = 15_000 }, TimeProvider.System);
    }
}