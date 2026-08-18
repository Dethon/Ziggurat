using System.Net.Http.Headers;
using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;

namespace Tests.Eval.Fixtures;

// Which provider actually answered, for the wire that does not say. The Responses stream carries
// only OpenRouter's generation id, and the name is one request away — a request no turn should
// ever pay for, so it is made here, off the turn's path, and only when something is being written
// that a person will read.
public static class ProviderLookup
{
    // The generation is not queryable the instant the stream ends; it lands a few seconds later.
    // Polling briefly is the difference between a dump that names the provider and one that says
    // "unknown" for a reason that has nothing to do with routing.
    private static readonly TimeSpan _delay = TimeSpan.FromSeconds(3);
    private const int Attempts = 4;

    public static async Task<ServedRoute?> ResolveAsync(ServedRoute? route, string apiKey)
    {
        if (route is null || route.Provider is not null || route.GenerationId is null)
        {
            return route;
        }

        using var client = new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        foreach (var attempt in Enumerable.Range(0, Attempts))
        {
            await Task.Delay(_delay * attempt);
            if (await ReadProviderAsync(client, route.GenerationId) is { } provider)
            {
                return route with { Provider = provider };
            }
        }

        return route;
    }

    private static async Task<string?> ReadProviderAsync(HttpClient client, string generationId)
    {
        try
        {
            var response = await client.GetAsync($"generation?id={Uri.EscapeDataString(generationId)}");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.TryGetProperty("data", out var data)
                   && data.TryGetProperty("provider_name", out var provider)
                ? provider.GetString()
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            // A diagnosis that could not name the provider is still a diagnosis; one that threw
            // while trying to would replace a behavioural failure with a network one.
            return null;
        }
    }
}