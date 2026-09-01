namespace Infrastructure.Agents.ChatClients;

// The one error a turn sent to the Lemonade chat host can fail with: the host could not be
// reached, timed out, or answered with an error. It names the host and its address so the person
// reading the red bubble knows which box to look at, and it never carries the wording of a
// cancellation, because WebChat's transient-error filter would swallow that and show nothing.
public sealed class LemonadeChatHostException(string address, string detail, Exception? inner = null)
    : Exception($"The Lemonade chat host at {address} did not answer this turn: {detail}", inner)
{
    public static LemonadeChatHostException From(string address, Exception cause) =>
        new(address, cause is OperationCanceledException || cause.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            ? "the request timed out"
            : cause.Message, cause);
}