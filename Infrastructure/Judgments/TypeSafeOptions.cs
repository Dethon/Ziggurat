namespace Infrastructure.Judgments;

// Where TypeSafe is and which Jev answers. The model is pinned to a version, never `jev-latest`:
// a bump changes what every judgment says and is a deliberate edit with a probe run and an eval
// pass behind it. An empty key is the feature off, not a startup failure.
// Bound straight from each host's `typeSafe` section, so the address and the pinned model are
// spelled once for every host that asks Jev.
public sealed record TypeSafeOptions
{
    public string ApiUrl { get; init; } = "https://api.typesafe.ai/v1/";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "jev-1.13.0";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    // A base address that does not end in a slash loses its last segment when a relative path is
    // resolved against it, so ".../v1" would post to "/systemone".
    public Uri BaseAddress => new(ApiUrl.TrimEnd('/') + "/");
}