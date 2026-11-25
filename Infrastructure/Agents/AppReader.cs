using Domain.Apps;
using Domain.Security;
using Domain.Tools.FileSystem.Bridge;

namespace Infrastructure.Agents;

// A built app's read, for the viewer the accounts service vouches for and the app the apps server
// names: through the building agent's mounts as a turn of it would see them, every call signed as the
// viewer (ReadRegistryCache over AgentReadRegistries), each operation the file tool's own (AppRead).
public sealed class AppReader(AssertionVerifier assertions, ReadRegistryCache registries, VfsBridgeSettings bridge)
{
    // Null where the assertion is not believed: none, a forgery, out of date, another channel's, the
    // guest's or nobody's. Who reads is its person, of its household, and nothing else the request says.
    public async Task<AppReadAnswer?> ReadAsync(
        string? assertion, string operation, AppReadRequest request, CancellationToken ct)
    {
        if (await assertions.VerifyTurnAsync(assertion, AppChannels.Apps, ct)
            is not TurnAdmission.Admitted { Identity: { Account: not null } viewer })
        {
            return null;
        }

        await using var lease = await registries.LeaseAsync(viewer, request.App.Agent, ct);
        return await new AppRead(lease.Registry.Call(bridge.MaxFileBytes)).RunAsync(operation, request.Arguments, ct);
    }
}