namespace Domain.Contracts;

// The alerts the agent is ringing now, found by id when their origin stops one. A table that
// outlives every ring and is found by key is state, so it lives in Infrastructure (RingingAlerts);
// what a ring does is AlertRelay's.
public interface IRingingAlerts
{
    // What the origin stopping it cancels; null when the id rings already, since a second ring of
    // one alert is the same alert and is offered once.
    CancellationToken? Begin(string alertId);

    // Nothing for an id not ringing: an alert that ended meanwhile has nothing left to stop.
    Task StopAsync(string alertId);

    void End(string alertId);
}