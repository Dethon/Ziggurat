namespace Infrastructure.Judgments;

// Where Jev is asked and which Jev answers. It is asked through OpenRouter, on the key every other
// hosted call uses, so there is one provider to pay and one connection to keep warm. The model is
// pinned to a dated snapshot, never `typesafe/jev-latest` and not the bare `typesafe/jev-1.13`
// either, which moves when TypeSafe patches it: a bump changes what every judgment says and is a
// deliberate edit with a probe run and an eval pass behind it.
// Bound straight from each host's `typeSafe` section, so the address and the pinned model are
// spelled once for every host that asks Jev. The key is not in that section: it is the host's
// `openRouter:apiKey`, handed over by `KeyedBy`, and empty is the feature off, not a startup
// failure.
public sealed record TypeSafeOptions
{
    public string ApiUrl { get; init; } = "https://openrouter.ai/api/";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "typesafe/jev-1.13-20260917";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    // A base address that does not end in a slash loses its last segment when a relative path is
    // resolved against it, so ".../api" would post to "/alpha/decisions".
    public Uri BaseAddress => new(ApiUrl.TrimEnd('/') + "/");

    public TypeSafeOptions KeyedBy(string openRouterApiKey) => this with { ApiKey = openRouterApiKey };
}

// The one OpenRouter setting a host that makes no chat call still needs: the key Jev is asked on.
// Bound from `OpenRouter:ApiKey`, which is the OPENROUTER__APIKEY line every container already reads.
public sealed record OpenRouterKey
{
    public string ApiKey { get; init; } = "";
}