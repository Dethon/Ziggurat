namespace Domain.DTOs;

// The hub's record that one outpost is live: a claim with a lifetime rather than a piece of
// configuration. Nothing deletes it when the machine dies — it simply stops being renewed.
//
// The name is the identity and the last write wins. A machine that restarts re-registers over its
// own entry, which is the common case and needs no handling at all; two machines sharing a name
// steal the mount from each other, which is accepted.
public sealed record OutpostRegistration
{
    public required string Name { get; init; }

    // The MCP address the hub dials to reach the machine. The outpost works it out from the route
    // toward the hub, or is told it outright, because nothing here can guess which interface will
    // answer.
    public required string Endpoint { get; init; }

    // The hub's answer to "did my mount actually happen", written when a session is built and read
    // back by the next keepalive. Unknown until an opted-in agent has built one.
    public OutpostVerdict Verdict { get; init; } = OutpostVerdict.Unknown;
}