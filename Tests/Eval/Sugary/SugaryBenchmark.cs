using System.Collections.Concurrent;
using Domain.Tools.Sugary.Estimation;
using Infrastructure.Agents.ChatClients;
using McpServerSugary.Channel;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Sugary;

// The benchmark meals as turns of the shipped sugary agent. Each meal is one turn on a stack of
// its own, sent the way the Sugary channel sends an estimate — the standalone app's words for the
// meal, the meal_estimate format, the photos as attachments the channel holds — and read the way
// the channel reads the answer. Nothing about the agent is assembled here: the model, the
// reasoning effort, the prompt and the routing are the deployment's, with the model swapped for the
// one the pass names.
public static class SugaryBenchmark
{
    // The agent the Sugary channel's turns are routed to (agentDefaults.byChannel.sugary).
    public const string Agent = "sugary";

    // How many of a meal's photos to send. One unless asked: the standalone figures this tier is
    // compared with (about 13–14 g from a photo) were measured on one photo per meal, and four
    // angles of an `nvr` plate is a different measurement.
    public const string PhotosVariable = "ZIGGURAT_EVAL_SUGARY_PHOTOS";

    public static int PhotosPerCase(string? variable) =>
        int.TryParse(variable, out var photos) && photos > 0 ? photos : 1;

    // Why this run is not the tier, or null when it is. The tier compares models, so it is asked
    // for with one named: a pass on whatever the definition ships would file a scorecard nobody
    // chose to compare. And a meal whose photo is not on disk would be estimated from the sentence
    // announcing it, so missing photos close the tier rather than lowering its score.
    public static string? WhyNot(bool armed, string? apiKey, string? model, int missingPhotos) =>
        !armed
            ? EvalGate.Reason
            : apiKey is null
                ? "openRouter:apiKey is not set in user secrets"
                : string.IsNullOrWhiteSpace(model)
                    ? $"the Sugary tier scores a model by name: set {EvalModel.Variable}=<openrouter id> " +
                      "(the shipped one is google/gemini-3.8-flash)"
                    : missingPhotos > 0
                        ? $"{missingPhotos} of the benchmark's photos are not in {SugaryCases.PhotoDirectory}: " +
                          "they are not in git, see Tests/Eval/Sugary/README.md for where they come from"
                        : null;

    public static int MissingPhotos(IEnumerable<SugaryCase> cases, int photosPerCase) =>
        cases.SelectMany(c => c.Photos.Take(photosPerCase))
            .Count(photo => !File.Exists(Path.Combine(SugaryCases.Directory, photo)));

    // One meal as the scenario the harness runs. Only the turn is the meal's; the rest says what
    // a run of it is, since the benchmark reads its answer rather than checking a contract.
    public static Scenario ScenarioOf(SugaryCase meal, int photosPerCase)
    {
        var photos = meal.Photos.Take(photosPerCase).ToList();
        return new Scenario
        {
            Name = $"benchmark: {meal.Name}",
            AgentId = Agent,
            Turn = new EvalTurn
            {
                // English, as the standalone benchmark asked: the truth is in grams, not words.
                Text = MealTurn.Text(new MealInput(photos.Count, meal.Description, null, "en")),
                Sender = SugaryChannel.Sender,
                Channel = Agent,
                ResponseFormat = MealEstimateSchema.Format,
                Attachments =
                [
                    .. photos.Select(photo => new EvalAttachment(Path.Combine(SugaryCases.Directory, photo), MediaType(photo))),
                    .. meal.Clip is { } clip ? [new EvalAttachment(Path.Combine(SugaryCases.Directory, clip), "video/mp4")] : Array.Empty<EvalAttachment>()
                ]
            },
            Instant = EvalInstant.Evening,
            // An estimate turn may read her log before answering (ADR-0044 lets a formatted turn
            // call tools), so the calls are counted rather than capped.
            CallCeiling = 4,
            CeilingReported = true,
            Group = meal.Set,
            Tier = EvalTier.Sugary
        };
    }

    // What one run of a meal produced, read as the channel would read it.
    public static SugaryReading Read(SugaryCase meal, Scenario scenario, Recording recording)
    {
        var sent = scenario.Turn.Attachments.Count;
        return recording.TimedOut
            ? SugaryReading.Failed(meal, $"the turn never finished within {scenario.Budget.TotalSeconds:0}s",
                recording.Elapsed, recording.Spend, sent)
            : recording.ProviderError is { } refused
                ? SugaryReading.Failed(meal, $"the provider refused the turn: {refused}", recording.Elapsed,
                    recording.Spend, sent)
                : SugaryReading.Of(meal, recording.Answer, recording.Elapsed, recording.Spend, sent,
                    recording.AttachmentsServed.Count);
    }

    // Every meal at once, each in a stack of its own: the gate EvalRun holds bounds how many stand
    // at a time (ZIGGURAT_EVAL_PARALLELISM), as it does for the scenarios. Read back in the cases'
    // order, so two passes list their meals the same way. A meal whose run throws is a failed meal
    // with the exception as its reason: one refusal must not lose the rest of a paid pass.
    public static async Task<SugaryPass> RunAsync(
        IReadOnlyList<SugaryCase> cases, int photosPerCase, Func<Scenario, Task<Recording>>? execute = null)
    {
        execute ??= EvalRun.ExecuteAsync;
        var recordings = new ConcurrentDictionary<int, Recording>();
        var readings = await Task.WhenAll(cases.Select(async (meal, index) =>
        {
            var scenario = ScenarioOf(meal, photosPerCase);
            try
            {
                var recording = await execute(scenario);
                recordings[index] = recording;
                return Read(meal, scenario, recording);
            }
            catch (Exception e)
            {
                return SugaryReading.Failed(
                    meal, $"the run threw {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}",
                    TimeSpan.Zero, Spend.Nothing, scenario.Turn.Attachments.Count);
            }
        }));

        var ordered = recordings.OrderBy(run => run.Key).Select(run => run.Value).ToList();
        return new SugaryPass(
            readings,
            ordered.Select(recording => recording.Route).LastOrDefault(route => route is not null),
            ordered.Select(recording => recording.PreloadModel).LastOrDefault(model => model is not null));
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };
}

public sealed record SugaryPass(IReadOnlyList<SugaryReading> Readings, ServedRoute? Route, string? PreloadModel);