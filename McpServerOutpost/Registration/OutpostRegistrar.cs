using Domain.DTOs;
using Domain.Outposts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerOutpost.Registration;

// The machine announcing itself, for as long as it runs. Running the binary is the whole
// installation: nothing is added to any configuration file, and this is what makes that true.
//
// It is a background service rather than a startup step because the listener has to come up
// whether or not the hub answers — a laptop that booted before the stack, or joined the VPN a
// minute late, must not be permanently absent for that reason. Registration retries forever, so a
// hub that comes back finds the machine already there, and a failed keepalive is just the next
// retry.
internal sealed class OutpostRegistrar(
    IOutpostAnnouncer announcer,
    OutpostRegistration registration,
    TimeProvider timeProvider,
    ILogger<OutpostRegistrar> logger) : BackgroundService
{
    // Short enough that a hub coming up a second after the machine is found almost at once, and
    // doubled up to the keepalive interval, which is the longest anything here ever waits.
    private static readonly TimeSpan _firstRetry = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var live = false;
        var retry = _firstRetry;

        while (!stoppingToken.IsCancellationRequested)
        {
            live = live ? await KeepAliveAsync(stoppingToken) : await RegisterAsync(stoppingToken);

            try
            {
                await Task.Delay(
                    live ? OutpostLifetime.KeepAliveInterval : retry, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // One, two, four … up to the keepalive interval, which is the longest anything here
            // ever waits: a hub that comes back is found within one interval whatever went wrong.
            // Reset once the machine is live, so the next outage starts fast again.
            retry = live ? _firstRetry : Backoff(retry);
        }
    }

    // A machine somebody switched off deliberately disappears at once rather than lingering as a
    // mount the agent offers for another ninety seconds. Best effort by construction: the hub may
    // be the thing that went away, and a shutdown must not hang on it.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await announcer.DeregisterAsync(registration.Name, cancellationToken);
            logger.LogInformation(
                "Outpost {Name} took its registration back; the agent stops offering it at once",
                registration.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Outpost {Name} could not take its registration back, so it lapses on its own within "
                + "{Expiry}", registration.Name, OutpostLifetime.Expiry);
        }
    }

    private async Task<bool> RegisterAsync(CancellationToken ct)
    {
        try
        {
            if (await announcer.RegisterAsync(registration, ct))
            {
                logger.LogInformation(
                    "Outpost {Name} registered at {Endpoint}", registration.Name, registration.Endpoint);
                return true;
            }

            logger.LogWarning(
                "The hub refused outpost {Name}'s registration; retrying", registration.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Outpost {Name} could not reach the hub to register; it keeps serving and keeps trying",
                registration.Name);
        }

        return false;
    }

    private async Task<bool> KeepAliveAsync(CancellationToken ct)
    {
        try
        {
            switch (await announcer.KeepAliveAsync(registration.Name, ct))
            {
                case KeepAliveOutcome.Refreshed:
                    return true;
                case KeepAliveOutcome.Lapsed:
                    // The hub forgot this machine while it was quiet. Dropping back to registering
                    // is the whole recovery: the name is the identity and the last write wins.
                    logger.LogInformation(
                        "Outpost {Name}'s registration had lapsed at the hub; announcing it again",
                        registration.Name);
                    return false;
                default:
                    logger.LogWarning(
                        "Outpost {Name} could not reach the hub to keep its registration alive",
                        registration.Name);
                    return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Outpost {Name} could not reach the hub to keep its registration alive", registration.Name);
            return false;
        }
    }

    private static TimeSpan Backoff(TimeSpan previous)
    {
        var doubled = previous * 2;
        return doubled > OutpostLifetime.KeepAliveInterval ? OutpostLifetime.KeepAliveInterval : doubled;
    }
}