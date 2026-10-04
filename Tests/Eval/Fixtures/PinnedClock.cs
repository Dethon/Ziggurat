using Microsoft.Extensions.Time.Testing;

namespace Tests.Eval.Fixtures;

// The scenario's instant for anything that asks what time it is, with real timers for anything
// that waits. A server that only reads the clock — the agent's "today is", a calendar listing's
// default window — must agree with the turn's timestamp; one that also retries on a delay would
// hang on a fake clock nothing advances.
public sealed class PinnedClock(FakeTimeProvider clock) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;
}