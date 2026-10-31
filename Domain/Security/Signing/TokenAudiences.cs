namespace Domain.Security.Signing;

// The `aud` an assertion is signed for. One for every consumer — the agent at a turn's start and the
// apps behind the proxy alike — because the accounts service signs it before it knows which of them
// the request is on its way to; what binds an assertion to one use is the channel it names. A call
// token, by contrast, is bound to the one server it is for.
public static class TokenAudiences
{
    public const string Assertion = "ziggurat-assertion";

    // A delegation's: the agent is its only reader, at fire time, whichever server kept it. What
    // binds one to one use is the object it names and the kind of fire that object raises.
    public const string Delegation = "ziggurat-delegation";

    // A call token's: the one server it is for, by the name it answers to (ServerAddress.Name) — the
    // name the agent keys that server's credential by (McpCredentials) and the one the server binds as
    // its own (MCP__AUDIENCE).
    public static string CallTo(ServerAddress server) => server.Name;
}