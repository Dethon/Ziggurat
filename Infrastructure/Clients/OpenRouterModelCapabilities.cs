using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients;

// The provider's own answer to "what does this model accept?", fetched and kept. Three states
// matter and they are deliberately different: a model the provider described is answered exactly,
// a model it never listed is answered permissively, and a lookup that has never once succeeded
// leaves everything permissive. Failing open is the point — a blip at the provider must not
// remove attachments from everyone.
public sealed class OpenRouterModelCapabilities(
    HttpClient httpClient,
    ILogger<OpenRouterModelCapabilities> logger) : IModelCapabilityCatalog
{
    private volatile IReadOnlyDictionary<string, IReadOnlyList<AttachmentKind>>? _accepted;

    public IReadOnlyList<AttachmentKind> GetAcceptedAttachmentKinds(string modelId)
    {
        var accepted = _accepted;
        if (accepted is null)
        {
            return AttachmentKinds.All;
        }

        return accepted.TryGetValue(Normalize(modelId), out var kinds) ? kinds : AttachmentKinds.All;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync("models", ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var parsed = await ParseAsync(stream, ct);
            if (parsed.Count == 0)
            {
                logger.LogWarning("The provider's model list held no models; keeping the previous capabilities");
                return;
            }

            _accepted = parsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                _accepted is null
                    ? "Reading the provider's model list failed and nothing is cached; attachment capability stays permissive"
                    : "Reading the provider's model list failed; keeping the last values that worked");
        }
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<AttachmentKind>>> ParseAsync(
        Stream stream, CancellationToken ct)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, IReadOnlyList<AttachmentKind>>();
        }

        return data.EnumerateArray()
            .Select(model => (
                Id: model.TryGetProperty("id", out var id) ? id.GetString() : null,
                Kinds: ReadKinds(model)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => Normalize(x.Id!))
            .ToDictionary(g => g.Key, g => g.First().Kinds);
    }

    private static IReadOnlyList<AttachmentKind> ReadKinds(JsonElement model)
    {
        if (!model.TryGetProperty("architecture", out var architecture)
            || !architecture.TryGetProperty("input_modalities", out var modalities)
            || modalities.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return modalities.EnumerateArray()
            .Select(m => m.GetString())
            .Select(ToKind)
            .OfType<AttachmentKind>()
            .Distinct()
            .ToList();
    }

    private static AttachmentKind? ToKind(string? modality) => modality?.ToLowerInvariant() switch
    {
        "image" => AttachmentKind.Image,
        "file" => AttachmentKind.Document,
        _ => null
    };

    // A leading tilde marks a routing alias in this repo's model configuration; the model it names
    // is what the provider lists.
    private static string Normalize(string modelId) => modelId.TrimStart('~').ToLowerInvariant();
}