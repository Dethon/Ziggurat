namespace Domain.Security.Signing;

// The `aud` an assertion is signed for. One for every consumer — the agent at a turn's start and the
// apps behind the proxy alike — because the accounts service signs it before it knows which of them
// the request is on its way to; what binds an assertion to one use is the channel it names. A call
// token, by contrast, is bound to the one server it is for.
public static class TokenAudiences
{
    public const string Assertion = "ziggurat-assertion";
}