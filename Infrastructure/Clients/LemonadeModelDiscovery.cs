using System.Net.Http.Headers;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients;

// The Lemonade chat host's own answer to "which chat models do you have?", fetched on a timer and
// kept. Unlike the hosted provider's capability catalogue this fails closed: a host that cannot be
// asked offers nothing, and it keeps offering nothing until an answer arrives, because a model the
// box cannot serve must not stay in anybody's menu. The refresh is the only network operation;
// reading Current never blocks.
public sealed class LemonadeModelDiscovery : ILemonadeModelSource
{
    private static readonly string[] _requiredLabels = ["chat", "tool-calling"];

    private readonly HttpClient _httpClient;
    private readonly LemonadeChatHostOptions _host;
    private readonly ILogger<LemonadeModelDiscovery> _logger;
    private volatile IReadOnlyList<LemonadeModel> _current = [];
    private bool _warned;

    public LemonadeModelDiscovery(
        HttpClient httpClient, LemonadeChatHostOptions host, ILogger<LemonadeModelDiscovery> logger)
    {
        _httpClient = httpClient;
        _host = host;
        _logger = logger;
        if (!host.IsConfigured)
        {
            return;
        }

        httpClient.BaseAddress = host.BaseAddress;
        // The same rule the memory embedding client follows: a box with no key to check gets no
        // header, so nothing is put on the wire for nothing.
        httpClient.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(host.ApiKey)
            ? null
            : new AuthenticationHeaderValue("Bearer", host.ApiKey);
    }

    public IReadOnlyList<LemonadeModel> Current => _current;

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!_host.IsConfigured)
        {
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync("models", ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            _current = await ParseAsync(stream, ct);
            _warned = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _current = [];
            // Once per outage rather than once per tick: a box that is off overnight is one line.
            if (!_warned)
            {
                _warned = true;
                _logger.LogWarning(ex,
                    "The Lemonade chat host at {Address} could not be asked for its models; offering none until it answers",
                    _host.Address);
            }
        }
    }

    private static async Task<IReadOnlyList<LemonadeModel>> ParseAsync(Stream stream, CancellationToken ct)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The models listing carried no data array");
        }

        return data.EnumerateArray()
            .Where(IsOffered)
            .Select(model => new LemonadeModel(
                model.GetProperty("id").GetString()!,
                Labels(model).Contains("vision", StringComparer.OrdinalIgnoreCase)
                    ? [AttachmentKind.Image]
                    : [],
                ReadInt(model, "context_length") ?? ReadInt(model, "max_context_window")))
            .ToList();
    }

    // A chat model that can call tools, already on disk, and the box's own: an embedding model
    // cannot answer, one not downloaded would first have to be fetched, and one the box proxies
    // from a cloud provider is not what "Lemonade" means in the menu.
    private static bool IsOffered(JsonElement model)
    {
        if (!model.TryGetProperty("id", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
        {
            return false;
        }

        var labels = Labels(model);
        return _requiredLabels.All(required => labels.Contains(required, StringComparer.OrdinalIgnoreCase))
               && model.TryGetProperty("downloaded", out var downloaded) && downloaded.ValueKind == JsonValueKind.True
               && !IsCloud(model, labels);
    }

    private static bool IsCloud(JsonElement model, IReadOnlyList<string> labels) =>
        labels.Contains("cloud", StringComparer.OrdinalIgnoreCase)
        || ReadString(model, "recipe") is "cloud"
        || !string.IsNullOrWhiteSpace(ReadString(model, "provider"));

    private static IReadOnlyList<string> Labels(JsonElement model) =>
        model.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
            ? labels.EnumerateArray().Select(l => l.GetString()).OfType<string>().ToList()
            : [];

    private static string? ReadString(JsonElement model, string name) =>
        model.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement model, string name) =>
        model.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;
}