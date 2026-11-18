using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using McpServerSugary.Store;

namespace McpServerSugary.Glucose;

// What one poll of a person's link came to, for her watches: the readings it kept (oldest first; none
// after a failure, and none when everything it was shown was already kept), the newest reading
// LibreLinkUp gave — the connection's current one, kept or not, since one the sensor has not replaced
// is given again and kept only once — and the link as the poll left it.
public sealed record GlucosePoll(
    Account Person,
    IReadOnlyList<SensorReading> Stored,
    GlucoseLinkState Link,
    SensorReading? Newest);

// The poller's half of a link: when LibreLinkUp last answered it, and since when and why it has not.
public sealed record GlucoseLinkState(
    DateTimeOffset LinkedAt,
    DateTimeOffset? LastSuccessAt,
    GlucoseLinkFailure? LastFailure,
    string? FailureDetail,
    DateTimeOffset? FailingSince);

// Told after each poll of a person's link whose answer was written, a failure's included; a poll that
// outlived the link it was for (she linked again meanwhile) tells nothing. One person's observer
// throwing is logged and touches nobody else's poll.
public interface IGlucosePollObserver
{
    Task PolledAsync(GlucosePoll poll, CancellationToken ct);
}