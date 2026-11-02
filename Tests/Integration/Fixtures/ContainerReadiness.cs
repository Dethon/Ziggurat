namespace Tests.Integration.Fixtures;

// How long a fixture waits for a container it started to be ready: polled, so a quiet machine pays the
// seconds it takes, and bounded by a budget only a broken container exhausts. The suite starts every
// fixture's container at once, beside whatever else the machine is running, and a fixed count of tries
// ran out there for a Jackett that answers in four seconds alone.
public static class ContainerReadiness
{
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
}