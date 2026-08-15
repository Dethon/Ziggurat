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

    // The last verdict reported, so a machine left running says it once rather than every thirty
    // seconds. Only the registrar's own loop touches it.
    private OutpostVerdict _reported = OutpostVerdict.Unknown;

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

    // Whether this was a shutdown is a question about the token, never about the exception's type.
    // An HttpClient whose timeout elapses throws TaskCanceledException — a cancellation nobody
    // asked for — so a filter reading the type let a slow hub out of here, faulted the background
    // service and stopped the host with it: the machine stopped serving its files because the hub
    // was slow, which is the one thing the retry loop exists to prevent. A genuine cancellation
    // still propagates, because the token says so.
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Outpost {Name} could not reach the hub to register; it keeps serving and keeps trying",
                registration.Name);
        }

        return false;
    }

    // The token, not the type, for the reason RegisterAsync gives.
    private async Task<bool> KeepAliveAsync(CancellationToken ct)
    {
        try
        {
            var answer = await announcer.KeepAliveAsync(registration.Name, ct);
            switch (answer.Outcome)
            {
                case KeepAliveOutcome.Refreshed:
                    Report(answer.Verdict);
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Outpost {Name} could not reach the hub to keep its registration alive", registration.Name);
            return false;
        }
    }

    // Somebody who runs an outpost and finds it never shows up can see why here, on the computer
    // they are sitting at, instead of needing the hub's logs. Logged when it changes, so a machine
    // left running does not repeat itself every thirty seconds.
    //
    // Unknown is not a problem and is not reported as one: it is what every registration reads as
    // until an opted-in agent has built a session, which may be a while on a hub nobody is talking
    // to. Shadowed is an error, because the machine is doing everything right and is still not
    // there, and only its operator can fix it — by starting it under a different name.
    private void Report(OutpostVerdict verdict)
    {
        if (verdict == _reported)
        {
            return;
        }

        _reported = verdict;
        switch (verdict)
        {
            case OutpostVerdict.Mounted:
                logger.LogInformation(
                    "Outpost {Name} is mounted: the agent can reach this machine's files",
                    registration.Name);
                break;
            case OutpostVerdict.Shadowed:
                logger.LogError(
                    "Outpost {Name} is SHADOWED: the agent already has a mount called '{Name}', so "
                    + "this machine is registered and not mounted. Stop it and start it again under "
                    + "a different --name",
                    registration.Name, registration.Name);
                break;
        }
    }

    private static TimeSpan Backoff(TimeSpan previous)
    {
        var doubled = previous * 2;
        return doubled > OutpostLifetime.KeepAliveInterval ? OutpostLifetime.KeepAliveInterval : doubled;
    }
}