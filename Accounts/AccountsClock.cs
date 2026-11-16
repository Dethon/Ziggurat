namespace Accounts;

// The clock links expire and sessions are seen on. Its own type rather than the container's
// TimeProvider, so a test can move this service's time without moving Kestrel's.
public sealed record AccountsClock(TimeProvider Time);