using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tests.Eval.Harness;

// The judged half of a run: rubric checks a deterministic matcher cannot make, graded by a model
// that is not the one under test. The judge reads one run's material — turn, reply, calls,
// delegations, changed files — and answers one rubric with a JSON verdict. It runs inside the
// same k-of-N policy as everything else; there is no separate tier.
public static class Judge
{
    // Pinned deliberately, and to a different vendor than the deployment's model: two scorecards
    // are only a diff if the grader held still, and a judge that upgrades alongside the agent
    // grades every bump with a different eye. Bump it on purpose, never as a side effect.
    public const string Model = "anthropic/claude-sonnet-5";

    // What one call or file may cost the judge's context. Arguments carry page bodies and notes
    // carry whole recipes; the judgement is about shape, not about the middle of the payload.
    private const int ArgumentCap = 2_000;
    private const int FileCap = 4_000;

    private const string SystemPrompt =
        "You are grading one recorded run of an assistant against one rubric. Grade ONLY what "
        + "the rubric asks — not style, not other rules, not what you would have done. Answer "
        + "with exactly one JSON object: {\"pass\": true|false, \"reason\": \"one sentence\"}. "
        + "If the material does not contain what the rubric needs, fail and say what is missing.";

    public static async Task<IReadOnlyList<string>> FailuresAsync(
        Scenario scenario, Recording recording, string apiKey, HttpMessageHandler? transport = null)
    {
        using var client = new HttpClient(
            transport ?? new SocketsHttpHandler(), disposeHandler: transport is null)
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/")
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var verdicts = new List<string>();
        foreach (var check in scenario.Judged)
        {
            var verdict = await AskAsync(client, Material(scenario, recording, check));
            if (verdict is null)
            {
                // Never a pass: a judge outage grading everything green would hide a regression
                // behind a network blip, which is the one thing a check must not do.
                verdicts.Add($"judged '{check.Claim}': the judge could not answer after a retry");
            }
            else if (!verdict.Pass)
            {
                verdicts.Add($"judged '{check.Claim}': {verdict.Reason}");
            }
        }

        return verdicts;
    }

    // Everything the judge is shown, in one string a test can pin. The material is the whole
    // world: the judge knows nothing about the suite, so a fact the rubric needs and the material
    // lacks is a fail by instruction.
    public static string Material(Scenario scenario, Recording recording, JudgedCheck check)
    {
        var calls = recording.Calls.Count == 0
            ? "- none"
            : string.Join("\n", recording.Calls.Select(call =>
                $"- {call.ToolName} {Capped(call.Arguments, ArgumentCap)}"));

        var delegations = recording.Delegations.Count == 0
            ? ""
            : "\n## Work handed to workers\n"
              + string.Join("\n", recording.Delegations.Select(d =>
                  $"- {d.ProfileId} was told: \"{d.Prompt}\""))
              + $"\n(every worker answered: \"{scenario.WorkerAnswer}\")\n";

        var changed = recording.FilesAfter.Keys.Union(recording.FilesBefore.Keys)
            .Where(path => recording.FilesBefore.GetValueOrDefault(path)
                           != recording.FilesAfter.GetValueOrDefault(path))
            .ToList();
        var files = changed.Count == 0
            ? ""
            : "\n## Files the turn changed\n"
              + string.Join("\n", changed.Select(path =>
                  $"### {path}\nBefore:\n```\n{Capped(recording.FilesBefore.GetValueOrDefault(path) ?? "(did not exist)", FileCap)}\n```\n"
                  + $"After:\n```\n{Capped(recording.FilesAfter.GetValueOrDefault(path) ?? "(deleted)", FileCap)}\n```"));

        return $"""
                ## Rubric
                {check.Rubric}

                ## The user's turn
                {scenario.Turn.Text}

                ## The assistant's reply
                {recording.Reply}

                ## Tool calls, in order
                {calls}
                {delegations}{files}
                """;
    }

    private static async Task<Verdict?> AskAsync(HttpClient client, string material)
    {
        // One retry, then a loud failure. The transient shapes worth absorbing are a non-200 and
        // a malformed body; anything past two attempts is an outage the run should report.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            try
            {
                var body = new JsonObject
                {
                    ["model"] = Model,
                    ["temperature"] = 0,
                    ["messages"] = new JsonArray(
                        new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                        new JsonObject { ["role"] = "user", ["content"] = material })
                };

                using var response = await client.PostAsync("chat/completions",
                    new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var content = answer.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (Parse(content) is { } verdict)
                {
                    return verdict;
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException
                    or KeyNotFoundException or InvalidOperationException)
            {
                // Fall through to the retry; the second miss returns null and fails loudly.
            }
        }

        return null;
    }

    // The verdict out of whatever prose the model wrapped it in: the first JSON object carrying a
    // boolean `pass`. A model that answered anything else has not answered.
    private static Verdict? Parse(string? content)
    {
        var start = content?.IndexOf('{') ?? -1;
        var end = content?.LastIndexOf('}') ?? -1;
        if (content is null || start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var verdict = JsonDocument.Parse(content[start..(end + 1)]);
            return verdict.RootElement.TryGetProperty("pass", out var pass)
                   && pass.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new Verdict(
                    pass.GetBoolean(),
                    verdict.RootElement.TryGetProperty("reason", out var reason)
                        ? reason.GetString() ?? "no reason given"
                        : "no reason given")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Capped(string text, int cap) =>
        text.Length <= cap ? text : text[..cap] + "…(truncated)";

    private sealed record Verdict(bool Pass, string Reason);
}