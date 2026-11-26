using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Monitor;
using Domain.Security;
using Domain.Tools;
using Domain.Tools.FileSystem.Bridge;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// A built app's read, for the viewer the accounts service vouches for — or, for a check, the person the
// agent's own token for the call that ran it names (CheckCallVerifier) — and the app the apps server
// names: through the building agent's mounts as a turn of it would see them, every call signed as the
// viewer (ReadRegistryCache over AgentReadRegistries), each operation the file tool's own (AppRead).
//
// The household decides on every read whether the agent acts for the viewer at all, as for a turn
// (HouseholdStanding): the assertion an app session holds may be five minutes old.
public sealed class AppReader(
    AssertionVerifier assertions, CheckCallVerifier checks, HouseholdStanding standing, ReadRegistryCache registries, VfsBridgeSettings bridge,
    ILogger<AppReader> logger)
{
    // Null where who reads is not believed: an assertion that is none, a forgery, out of date, another
    // channel's, the guest's or nobody's; a check's call token that is not this agent's for the apps
    // server, naming a person; or both at once, which would leave two answers. Who reads is that person,
    // of their household, and nothing else the request says.
    public async Task<AppReadAnswer?> ReadAsync(
        string? assertion, string? checkCallToken, string operation, AppReadRequest request, CancellationToken ct)
    {
        var admission = (assertion, checkCallToken) switch
        {
            ({ }, { }) => new TurnAdmission.Refused(TurnAdmission.BothAnAssertionAndACheck),
            (null, { } token) => await checks.VerifyAsync(token, ct),
            _ => await assertions.VerifyTurnAsync(assertion, AppChannels.Apps, ct)
        };
        if (admission is not TurnAdmission.Admitted { Identity: { Account: not null } viewer })
        {
            logger.LogWarning("Refused a read of app {AppId}: its {Proof} is {Reason}", request.App.Id,
                checkCallToken is null ? "assertion" : "check's call token",
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

    // Only a standing that could not be read is worth asking again; every other refusal holds until somebody
    // changes it, as it does for a turn (ConversationGroup.RefuseByStandingAsync).
    internal static ToolErrorResult Refused(StandingRefusal refusal) => refusal switch
    {
        StandingRefusal.Suspended => ToolError.PermissionDenied(
            "The household is suspended, so its apps read nothing.", "Nothing to do here: the operator resumes a household."),
        StandingRefusal.Disabled => ToolError.PermissionDenied(
            "This person is disabled in the household, so the household's apps read nothing for them.",
            "An owner of the household enables them again."),
        StandingRefusal.SugaryOff => ToolError.PermissionDenied(
            "Sugary is turned off for the household, so this read was refused.",
            "An owner of the household turns Sugary back on on the Features page."),
        StandingRefusal.Unchecked => ToolError.TransientDependency(
            "Whether the household lets this read through could not be read just now, so nothing was read.",
            "Read again in a moment."),
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Every standing refusal is answered.")
    };
}