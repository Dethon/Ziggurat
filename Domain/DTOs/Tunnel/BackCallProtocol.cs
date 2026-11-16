namespace Domain.DTOs.Tunnel;

// What a household's server may ask the deployment for over its own tunnel, and nothing else: a
// back-call is a stream the server opens with one of these tags, carrying HTTP/1.1, which the relay
// answers only on its tag's paths. Spelled once here so both ends read the same names.
public static class BackCallProtocol
{
    // The accounts service's resolution and guest endpoints (voice): who a speaker is.
    public const string Accounts = "accounts";

    // The agent's exec bridge (the sandbox): a command's view of the session's other mounts.
    public const string VfsBridge = "vfs-bridge";

    // The metrics feed: a server's events, published onto the deployment's feed by the relay.
    public const string Metrics = "metrics";

    // The shared Lemonade's speech-to-text and text-to-speech (voice).
    public const string Lemonade = "lemonade";

    // The agent's topic minting (voice).
    public const string Topics = "topics";

    // Where a server posts one metric event, as JSON, on its metrics back-call.
    public const string MetricsPath = "/events";

    // Who asked, as the relay knows it from the tunnel's verified credential: set on every back-call
    // it lets through, over any the server sent under the same prefix.
    public const string HeaderPrefix = "X-Ziggurat-Back-Call-";
    public const string HouseholdHeader = HeaderPrefix + "Household";
    public const string ServerHeader = HeaderPrefix + "Server";
}