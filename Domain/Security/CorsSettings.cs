namespace Domain.Security;

// The origins a browser page may call the agent registration API and the hub from, with the
// visitor's credentials. Bound under the same section on both hosts — `Cors:AllowedOrigins`,
// `CORS__ALLOWEDORIGINS__0` and on in the environment — and a per-deployment value, since the
// origins are the deployment's own hosts. Empty admits no cross-origin call at all: same-origin
// only, so a deployment that never named its origins is not one any page on the web can drive.
public sealed record CorsSettings
{
    public string[] AllowedOrigins { get; init; } = [];
}