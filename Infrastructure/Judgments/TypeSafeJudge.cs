using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Judgments;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Judgments;

// The one client that talks to TypeSafe: `POST /v1/systemone` with the state, the pinned model
// and the questions keyed by id. No SDK exists for .NET, so this is a typed HttpClient over the
// documented JSON, and the only place the wire's names appear.
//
// Nothing here is retried and nothing is thrown at a caller for the service's sake: a 429, a
// 529, a timeout, a cut connection or a body that is not a judgment is an absence with a reason.
// A 401 or a 422 is different — the deployment is misconfigured, not the service unlucky — so
// it is logged as an error naming the setting to fix, once, and then answered as absence like
// the rest.
public sealed class TypeSafeJudge : IJudge
{
    public const string Endpoint = "systemone";

    // Answers the key's models and bills nothing: what a keep-alive against this host pings.
    public const string NonBillableEndpoint = "models";

    private readonly HttpClient _httpClient;
    private readonly TypeSafeOptions _options;
    private readonly ILogger _logger;
    // Each setting's rejection is logged once, so a key fixed and a model still wrong is heard.
    private readonly HashSet<string> _configurationErrorsLogged = [];

    private TypeSafeJudge(HttpClient httpClient, TypeSafeOptions options, ILogger logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        httpClient.BaseAddress = options.BaseAddress;
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    // The empty key registers a judge that answers absence, so nothing downstream checks the key.
    public static IJudge Create(HttpClient httpClient, TypeSafeOptions options, ILogger logger) =>
        options.IsConfigured
            ? new TypeSafeJudge(httpClient, options, logger)
            : UnconfiguredJudge.Instance;

    public async Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline)
    {
        if (deadline.IsCancellationRequested)
        {
            return new JudgmentOutcome.Absent(AbsenceReason.Deadline);
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(Endpoint, Wire(request), WireJson, deadline);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => Misconfigured("typeSafe:apiKey", response.StatusCode),
                HttpStatusCode.UnprocessableEntity => Misconfigured("typeSafe:model", response.StatusCode),
                _ when !response.IsSuccessStatusCode => Unavailable(response.StatusCode),
                _ => Parse(await response.Content.ReadFromJsonAsync<WireResponse>(WireJson, deadline))
            };
        }
        catch (OperationCanceledException)
        {
            // The caller's deadline or the client's own timeout — either way, time ran out.
            return new JudgmentOutcome.Absent(AbsenceReason.Deadline);
        }
        catch (Exception ex)
        {
            // Whatever the transport or the body did — a cut connection, a body that is not
            // JSON, a content type the reader refuses — is the service's failure, not the turn's.
            _logger.LogWarning(ex, "TypeSafe could not be reached or did not answer a judgment");
            return new JudgmentOutcome.Absent(AbsenceReason.Error);
        }
    }

    private JudgmentOutcome Misconfigured(string setting, HttpStatusCode status)
    {
        bool first;
        lock (_configurationErrorsLogged)
        {
            first = _configurationErrorsLogged.Add(setting);
        }

        if (first)
        {
            _logger.LogError(
                "TypeSafe rejected the deployment's configuration ({Status}): check {Setting} (model {Model})",
                (int)status, setting, _options.Model);
        }

        return new JudgmentOutcome.Absent(AbsenceReason.Error);
    }

    private JudgmentOutcome Unavailable(HttpStatusCode status)
    {
        _logger.LogWarning("TypeSafe answered {Status}; no judgment this call", (int)status);
        return new JudgmentOutcome.Absent(AbsenceReason.Error);
    }

    private JudgmentOutcome Parse(WireResponse? wire)
    {
        if (wire?.Answers is null || wire.Usage is null || wire.Model is null)
        {
            _logger.LogWarning("TypeSafe answered a body that is not a judgment");
            return new JudgmentOutcome.Absent(AbsenceReason.Error);
        }

        var answers = wire.Answers.ToDictionary(a => a.Key, a => Answer(a.Value));
        return new JudgmentOutcome.Answered(new Judgment(
            wire.Model,
            answers,
            new JudgmentUsage(wire.Usage.InputTokens, wire.Usage.OutputTokens)));
    }

    private static JudgmentAnswer Answer(WireAnswer answer) => answer.Type switch
    {
        "choice" => new ChoiceAnswer(
            answer.Choice ?? throw new JsonException("A choice answer carries no choice"),
            answer.Confidence ?? throw new JsonException("A choice answer carries no confidence"),
            answer.Probabilities ?? new Dictionary<string, double>()),
        "noul" => new NoulAnswer(answer.Noul ?? throw new JsonException("A noul answer carries no probability")),
        _ => throw new JsonException($"Unknown answer type '{answer.Type}'")
    };

    private JsonObject Wire(JudgmentRequest request) => new()
    {
        ["state"] = request.State.DeepClone(),
        ["model"] = _options.Model,
        ["questions"] = new JsonObject(request.Questions.Select(q =>
            KeyValuePair.Create<string, JsonNode?>(q.Key, Wire(q.Value))))
    };

    private static JsonObject Wire(JudgmentQuestion question) => question switch
    {
        ChoiceQuestion choice => new JsonObject
        {
            ["type"] = "choice",
            ["instructions"] = choice.Instructions,
            ["criteria"] = new JsonObject(choice.Criteria.Select(c =>
                KeyValuePair.Create<string, JsonNode?>(c.Key, c.Value)))
        },
        NoulQuestion noul => new JsonObject
        {
            ["type"] = "noul",
            ["instructions"] = noul.Instructions
        },
        _ => throw new ArgumentOutOfRangeException(nameof(question), question.GetType().Name, "Unknown question kind")
    };

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private sealed record WireResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("answers")] Dictionary<string, WireAnswer>? Answers,
        [property: JsonPropertyName("usage")] WireUsage? Usage);

    private sealed record WireAnswer(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("choice")] string? Choice,
        [property: JsonPropertyName("confidence")] double? Confidence,
        [property: JsonPropertyName("probabilities")] Dictionary<string, double>? Probabilities,
        [property: JsonPropertyName("noul")] double? Noul);

    private sealed record WireUsage(
        [property: JsonPropertyName("input_tokens")] int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens);

    private sealed class UnconfiguredJudge : IJudge
    {
        public static readonly UnconfiguredJudge Instance = new();

        public Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline) =>
            Task.FromResult<JudgmentOutcome>(new JudgmentOutcome.Absent(AbsenceReason.Unconfigured));
    }
}