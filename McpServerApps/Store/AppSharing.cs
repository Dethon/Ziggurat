using Domain.Contracts;
using Domain.DTOs.Household;

namespace McpServerApps.Store;

// Whether the household shares an app as an app space, read from its spaces section — the one truth of
// it; the apps server keeps no copy — on whatever proof the caller holds: a call's token for the mount, a
// viewer's own `apps` assertion for an origin. An outage, or the reader's own timeout (a cancellation the
// caller did not ask for), is Unreadable; the accounts service refusing the proof is Refused. What either
// costs is the caller's to say: an origin opens its author's app all the same, a delete deletes nothing.
public abstract record AppSharing
{
    private AppSharing() { }

    // SharedAs: the app space sharing the app, none when it is not shared.
    public sealed record Read(HouseholdSpace? SharedAs) : AppSharing;

    public sealed record Unreadable(Exception Cause) : AppSharing;

    public sealed record Refused(HouseholdConfigRefusedException Cause) : AppSharing;

    public static async Task<AppSharing> OfAsync(ICallHousehold household, BuiltApp app, CancellationToken ct)
    {
        try
        {
            var spaces = await household.SpacesAsync(ct);
            return new Read(spaces.Spaces.FirstOrDefault(space => space.AppId == app.Id));
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new Unreadable(ex);
        }
        catch (HouseholdConfigRefusedException ex)
        {
            return new Refused(ex);
        }
    }
}