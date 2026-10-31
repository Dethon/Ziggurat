using Domain.Security.Signing;

namespace Mcp.Hosting.Tunnel;

// What a tunnelled server checks a call token against: the audience and the agent's keys the relay
// handed over its tunnel, never a URL or a key set fetched from anywhere. Empty until the relay
// admits the tunnel, and then whatever it last said — a rotation's keys replace the ones before.
//
// Before any admission a token finds no keys (KeysUnavailable) and names no audience this server
// answers to: nothing reaches a tunnelled server's /mcp but its tunnel, so a call that arrives before
// the relay has spoken is not the agent's.
internal sealed class TunnelTrust : IVerificationKeys
{
    private volatile Delivered? _delivered;

    public string Audience => _delivered?.Audience ?? "";

    // Which household and server the relay admitted this tunnel as, for whatever else a server asks
    // (the back-calls and the logs).
    public string? TenantId => _delivered?.TenantId;

    public ValueTask<VerificationKey?> FindAsync(string keyId, CancellationToken ct) =>
        _delivered is { } delivered
            ? ValueTask.FromResult(delivered.Keys.GetValueOrDefault(keyId))
            : throw new SigningKeysUnavailableException("the relay has not handed this server the agent's keys yet");

    public void Admit(string tenantId, string audience, string keys) =>
        _delivered = new Delivered(tenantId, audience, Read(keys));

    public void Rotate(string keys)
    {
        if (_delivered is { } delivered)
        {
            _delivered = delivered with { Keys = Read(keys) };
        }
    }

    private static IReadOnlyDictionary<string, VerificationKey> Read(string json) =>
        JsonWebKeySet.Parse(json).Keys
            .Select(VerificationKey.From)
            .OfType<VerificationKey>()
            .DistinctBy(k => k.Id)
            .ToDictionary(k => k.Id);

    private sealed record Delivered(string TenantId, string Audience, IReadOnlyDictionary<string, VerificationKey> Keys);
}