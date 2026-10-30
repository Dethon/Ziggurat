namespace Domain.Security.Signing;

// The `aud` an assertion is signed for. One for every consumer — the agent at a turn's start and the
// apps behind the proxy alike — because the accounts service signs it before it knows which of them
// the request is on its way to; what binds an assertion to one use is the channel it names. A call
// token, by contrast, is bound to the one server it is for.
public static class TokenAudiences
{
    public const string Assertion = "ziggurat-assertion";

    // A call token's: the one server it is for, named by the host the agent dials it at — the name
    // the agent keys that server's credential by (McpCredentials) and the one the server binds as its
    // own (MCP__AUDIENCE). The host and not the port, so a test server on localhost is "localhost"
    // whatever port it took. Empty for an address that is not one, which no server answers to.
    public static string CallTo(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : "";
}