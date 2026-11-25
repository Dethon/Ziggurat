using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Monitor;
using Domain.Security;
using Domain.Tools;
using Domain.Tools.FileSystem.Bridge;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// A built app's read, for the viewer the accounts service vouches for and the app the apps server
// names: through the building agent's mounts as a turn of it would see them, every call signed as the
// viewer (ReadRegistryCache over AgentReadRegistries), each operation the file tool's own (AppRead).
//
// The household decides on every read whether the agent acts for the viewer at all, as for a turn
// (HouseholdStanding): the assertion an app session holds may be five minutes old.
public sealed class AppReader(
    AssertionVerifier assertions, HouseholdStanding standing, ReadRegistryCache registries, VfsBridgeSettings bridge,
    ILogger<AppReader> logger)
{
    // Null where the assertion is not believed: none, a forgery, out of date, another channel's, the
    // guest's or nobody's. Who reads is its person, of its household, and nothing else the request says.
    public async Task<AppReadAnswer?> ReadAsync(
        string? assertion, string operation, AppReadRequest request, CancellationToken ct)
    {
        var admission = await assertions.VerifyTurnAsync(assertion, AppChannels.Apps, ct);
        if (admission is not TurnAdmission.Admitted { Identity: { Account: not null } viewer })
        {
            logger.LogWarning("Refused a read of app {AppId}: its assertion is {Reason}", request.App.Id,
                admission is TurnAdmission.Refused { Reason: var reason } ? reason : "the guest's");
            return null;
        }

        if (await standing.RefusalAsync([viewer.TenantId], viewer, ct) is { } refusal)
        {
            return AppReadAnswer.Refused(Refused(refusal));
        }

        ReadLease lease;
        try
        {
            lease = await registries.LeaseAsync(viewer, request.App.Agent, ct);
        }
        catch (AgentNotFoundException)
        {
            return AppReadAnswer.Refused(ToolError.PermissionDenied(
                $"The household no longer has {request.App.Agent}, the agent that built this app, so the app reads nothing.",
                "Build the app again with one of the household's agents."));
        }

        await using (lease)
        {
            var registry = lease.Registry;
            return await new AppRead(registry.Call(viewer, bridge.MaxFileBytes), request.App.Reads, registry.Offline)
                .RunAsync(operation, request.Arguments, ct);
        }
    }

    private static ToolErrorResult Refused(StandingRefusal refusal) => refusal switch
    {
        StandingRefusal.Suspended => ToolError.PermissionDenied(
            "The household is suspended, so its apps read nothing.", "Nothing to do here: the operator resumes a household."),
        StandingRefusal.Disabled => ToolError.PermissionDenied(
            "This person is disabled in the household, so the household's apps read nothing for them.",
            "An owner of the household enables them again."),
        _ => ToolError.TransientDependency(
            "Whether the household lets this read through could not be read just now, so nothing was read.",
            "Read again in a moment.")
    };
}