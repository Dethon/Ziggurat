namespace Domain.Monitor;

// What ended the monitor, and whether starting it again could ever end differently.
public static class MonitorFault
{
    // Both ride the error family rather than a metric family of their own: a restart is what an
    // operator is looking for on the day the agent went quiet, and that is the feed they are
    // already reading. Distinct ErrorTypes, so each is its own bar in the by-type breakdown.
    public const string RestartErrorType = "MonitorRestart";
    public const string FatalErrorType = "MonitorFatal";

    // A dependency that is down comes back; a value the code cannot use does not. Retrying the
    // second is a hot loop dressed as resilience — the same exception, at the same place, for as
    // long as the deployment stands — so these end the host instead, and the container's restart
    // policy turns the fault into a visible crash rather than a log line nobody reads.
    //
    // Deliberately short. Everything not named here is treated as transient, because the cost of
    // waiting out a fault that will never clear is one process that retries slowly, while the cost
    // of calling a passing outage unrecoverable is an agent that stays down until somebody notices.
    public static bool IsFatal(Exception exception) =>
        Root(exception) is ArgumentException      // a value handed to the code is not one it can use
            or FormatException                    // and UriFormatException with it: a malformed endpoint
            or NotSupportedException
            or TypeInitializationException;       // a type that cannot be built once cannot be built later

    // What actually went wrong, rather than what carried it out. A fault on the monitor's side of
    // the merge arrives as a ChannelClosedException wrapping the real one, because that is how the
    // merge ends a channel it cannot go on filling — so classifying or reporting the outer
    // exception would name the plumbing in every log line and never match anything above.
    public static Exception Root(Exception exception) => exception switch
    {
        AggregateException aggregate when aggregate.InnerExceptions.Count > 0 =>
            Root(aggregate.InnerExceptions[0]),
        { InnerException: { } inner } => Root(inner),
        _ => exception
    };

    public static string Describe(Exception exception)
    {
        var root = Root(exception);
        return $"{root.GetType().Name}: {root.Message}";
    }
}