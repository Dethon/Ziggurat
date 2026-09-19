using System.Text.Json;
using Domain.DTOs.Metrics;
using Infrastructure.Judgments;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Integration.Clients;
using Tests.Integration.McpServers;

namespace Tests.Integration.McpChannelVoice;

// The probe as a test: the 32 labelled spoken answers from .scratch/jev-voice-approval/probe run
// through the real reader — the shipped questions, bars and word list — against live Jev. What is
// pinned hard is what the feature is set for: no answer decided the wrong way, because a wrong
// approval here runs a tool on a permission the person did not give, and a wrong refusal costs
// them the thing they asked for. A labelled yes or no that came back ambiguous is a re-ask, which
// is what happened before Jev; their count is reported and floored at what was measured the day
// this landed, so a question edit that loses ground is heard. Like Category=Llm it runs whenever
// a key is present; a pass costs well under a cent.
[Trait("Category", "Jev")]
public class ApprovalReaderJevTests
{
    // Measured on 2026-09-18 against jev-1.13.0 with the shipped wording and bars
    // (.scratch/jev-voice-approval/probe/2026-09-18-wordings-*.txt): "déjalo" and "never mind"
    // re-ask on every run, and "venga, dale" sits at the sure bar and flips — three on the worse
    // of the two runs. That number, so a wording that loses one more is heard.
    private const int ReAskCeiling = 3;

    // The two prompts the original 32 were labelled and the ceiling measured against. The set now
    // also carries the shapes the tool actually speaks — a bare tool-name suffix, several tool
    // names at once, and the "No entendí." re-ask — because the probe measured none of them, and
    // both questions ask about what the prompt "names" and whether a yes narrows it.
    private const string VaultPrompt = "¿Apruebas borrar siete notas del vault? Di sí o no.";
    private const string LightsPrompt = "¿Apruebas apagar las luces del salón y de la cocina? Di sí o no.";

    private static bool IsMeasuredPrompt(string prompt) => prompt is VaultPrompt or LightsPrompt;

    private static readonly Lazy<Task<IReadOnlyList<Verdict>>> _run = new(RunAsync);

    private sealed record Case(string Prompt, string Answer, string Want);

    private sealed record Verdict(Case Case, ApprovalReading Reading)
    {
        public bool WantsApproval => Case.Want == "approve";
        public bool WantsRefusal => Case.Want == "decline";

        // A tool run on a permission not given, or a clear yes refused.
        public bool IsWrongAction =>
            (Reading.Response == ApprovalResponse.Approved && !WantsApproval)
            || (Reading.Response == ApprovalResponse.Declined && WantsApproval);

        public bool IsReAsk => (WantsApproval || WantsRefusal) && Reading.Response == ApprovalResponse.Ambiguous;

        public override string ToString() =>
            $"'{Case.Answer}' wanted {Case.Want}, got {Reading.Response} by {ApprovalDeciders.Of(Reading.DecidedBy)} " +
            $"(approved={Reading.Approved:F2} declined={Reading.Declined:F2})";
    }

    [SkippableFact]
    public async Task OverTheLabelledAnswers_NoneIsDecidedTheWrongWay()
    {
        var verdicts = await _run.Value;

        verdicts.Where(v => v.IsWrongAction).Select(v => v.ToString()).ShouldBeEmpty();
    }

    // Only over the prompt the ceiling was measured against. The tool-name and re-ask cases are
    // held to no wrong action — the test above, which covers every case — but not to a re-ask count
    // nobody has measured; pinning one from a first run would pin whatever that run happened to do.
    [SkippableFact]
    public async Task OverTheLabelledAnswers_TheReAsksStayUnderTheMeasuredCeiling()
    {
        var verdicts = await _run.Value;

        var reAsks = verdicts
            .Where(v => v.IsReAsk && IsMeasuredPrompt(v.Case.Prompt))
            .Select(v => v.ToString())
            .ToList();
        reAsks.Count.ShouldBeLessThanOrEqualTo(ReAskCeiling, "re-asked:\n" + string.Join("\n", reAsks));
    }

    // The answers the word list could not read, now decided; the plain "sí sí" approved at once;
    // and the narrowed yes, which the word list approved, held back. The other narrowed answer,
    // "no, solo la del salón", declines by agreement — the rule's doing, and a refusal of the whole
    // is what it is, so nothing runs and the agent hears it.
    [SkippableFact]
    public async Task TheWordListsBlindSpots_AreDecided_AndTheNarrowedYesIsNot()
    {
        var verdicts = await _run.Value;

        // Scoped to the two prompts these answers were labelled against: the same words are now
        // also measured against the tool-name and re-ask prompts, where they are their own cases.
        ApprovalResponse of(string answer) => verdicts
            .Single(v => v.Case.Answer == answer && IsMeasuredPrompt(v.Case.Prompt)).Reading.Response;

        of("adelante").ShouldBe(ApprovalResponse.Approved);
        of("hazlo").ShouldBe(ApprovalResponse.Approved);
        of("mejor no").ShouldBe(ApprovalResponse.Declined);
        of("espera, no lo hagas").ShouldBe(ApprovalResponse.Declined);
        of("sí sí").ShouldBe(ApprovalResponse.Approved);
        of("sí, pero la de la cocina no").ShouldBe(ApprovalResponse.Ambiguous);
        of("no, solo la del salón").ShouldNotBe(ApprovalResponse.Approved);
    }

    private static async Task<IReadOnlyList<Verdict>> RunAsync()
    {
        var reader = ShippedReader(ModalJudgeJevTests.RequireKey());

        var cases = JsonSerializer.Deserialize<List<Case>>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Integration", "McpChannelVoice", "jev-approval-cases.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        cases.Count.ShouldBe(51);

        // A few at a time: the set is small and the service is shared.
        using var width = new SemaphoreSlim(4);
        return await Task.WhenAll(cases.Select(async c =>
        {
            await width.WaitAsync();
            try
            {
                var reading = await reader.ReadAsync(c.Prompt, c.Answer, turnModel: null, CancellationToken.None);
                reading.DecidedBy.ShouldNotBe(ApprovalDecider.WordList, $"Jev did not answer for '{c.Answer}'");
                return new Verdict(c, reading);
            }
            finally
            {
                width.Release();
            }
        }));
    }

    // The reader exactly as the voice channel ships it — its pinned model and its bars — with a
    // generous deadline, because what is measured here is the answer and not this network's tail.
    private static JudgedApprovalReader ShippedReader(string apiKey)
    {
        var shipped = new ConfigurationBuilder()
                          .AddJsonFile(Path.Combine(McpServerRegistrations.RepoRoot, "McpChannelVoice", "appsettings.json"))
                          .Build()
                          .Get<VoiceSettings>()
                      ?? throw new InvalidOperationException("appsettings.json did not bind");
        var judge = TypeSafeJudge.Create(
            new HttpClient(),
            new TypeSafeOptions { ApiUrl = shipped.TypeSafe.ApiUrl, ApiKey = apiKey, Model = shipped.TypeSafe.Model },
            NullLogger.Instance);
        return new JudgedApprovalReader(judge, shipped.Approval.Judgment with { DeadlineMs = 15_000 }, TimeProvider.System);
    }
}