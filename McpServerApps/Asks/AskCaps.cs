using Domain.Contracts;
using Domain.DTOs.Household;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.Extensions.Logging;

namespace McpServerApps.Asks;

// The daily cap an app's asks meet: the one place it is decided. The household's app asks section is its
// owners' word — an app's own cap, else the household's default — read on whatever proof the ask holds (a
// viewer's own `apps` assertion; a check's call token), and only what it leaves unset is the deployment's
// (Asks:DailyCap). What cannot be read refuses the ask rather than falling back to the deployment's, which
// would lift a cap an owner set lower — whatever went wrong, so no reader's failure is a 500 either.
public sealed class AskCaps(AppsSettings settings, ILogger<AskCaps> logger)
{
    public async Task<AskLimit> LimitAsync(BuiltApp app, ICallHousehold household, CancellationToken ct)
    {
        try
        {
            return new AskLimit.Read((await household.AppAsksAsync(ct)).LimitOf(app.Id, settings.Asks.DailyCap));
        }
        catch (HouseholdConfigRefusedException ex)
        {
            logger.LogWarning(ex, "The accounts service refused household {Household}'s app asks for app {App}", app.HouseholdId, app.Id);
            return AskLimit.Refused.Instance;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read household {Household}'s app asks for app {App}", app.HouseholdId, app.Id);
            return AskLimit.Unreadable.Instance;
        }
    }
}

// The limit an app's ask meets, or why it could not be known: the accounts service refusing the proof, or
// anything else (an outage, the reader's own timeout, an answer that does not parse).
public abstract record AskLimit
{
    private AskLimit() { }

    public sealed record Read(int Limit) : AskLimit;

    public sealed record Unreadable : AskLimit
    {
        public static readonly Unreadable Instance = new();
    }

    public sealed record Refused : AskLimit
    {
        public static readonly Refused Instance = new();
    }
}