namespace Infrastructure.Agents.ChatClients;

// The one error a turn sent to the Lemonade chat host can fail with: the host could not be
// reached, timed out, or answered with an error. It names the host and its address so the person
// reading the red bubble knows which box to look at, and it never carries the wording of a
// cancellation, because WebChat's transient-error filter would swallow that and show nothing.
public sealed class LemonadeChatHostException : Exception
{
    public LemonadeChatHostException(string address, string detail, Exception? inner = null)
        : base($"The Lemonade chat host at {address} did not answer this turn: {detail}", inner)
    {
    }

    private LemonadeChatHostException(string message) : base(message)
    {
    }

    public static LemonadeChatHostException From(string address, Exception cause) =>
        new(address, cause is OperationCanceledException || cause.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            ? "the request timed out"
            : cause.Message, cause);

    // No box serves the household at all — its own was removed, or a device kept a pick of another's —
    // so there is no address to name, and the person can do one thing about it.
    public static LemonadeChatHostException NoHost(string bareModel) =>
        new($"No Lemonade box serves this household now, so '{bareModel}' was not run: pick another model in the menu.");
}