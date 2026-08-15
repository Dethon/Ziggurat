using System.Text.Json.Serialization;

namespace Domain.DTOs;

// What became of an outpost's registration when a session was last built. There is exactly one way
// for an outpost to be registered and still not be there — its name is already some other mount's
// — and nothing at the machine can detect it, because the collision is discovered on the hub long
// after the registration succeeded. The keepalive's answer is the only channel back.
// By name on the wire, because this one crosses a process boundary: it is written into the
// keepalive's answer and read by a binary on somebody else's machine, and "Shadowed" is what
// somebody reading that response with curl needs to see. The converter still accepts a number, so
// a registration stored before this does not become unreadable.
[JsonConverter(typeof(JsonStringEnumConverter<OutpostVerdict>))]
public enum OutpostVerdict
{
    // No opted-in agent has built a session since this registration landed. Not a problem: it is
    // what every registration reads as for its first few seconds, and what one reads as forever on
    // a hub nobody is talking to.
    Unknown,

    Mounted,

    // Registered, valid, and not mounted. The existing mount always wins.
    Shadowed
}