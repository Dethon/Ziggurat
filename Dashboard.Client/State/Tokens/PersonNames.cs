using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Tokens;

// Whom an account stands for on a page that lists events: the latest name its model calls carried
// that is not the id itself. A memory call and Jev's judgments name only the account, a turn names the
// person; the breakdowns label people the same way server side (MetricsQueryService.PersonLabels).
public sealed class PersonNames
{
    private readonly IReadOnlyDictionary<string, string> _names;

    private PersonNames(IReadOnlyDictionary<string, string> names) => _names = names;

    public static PersonNames From(IEnumerable<TokenUsageEvent> calls) =>
        new(calls
            .Where(call => call.UserId is { } id && call.Sender != id && !ServiceSenders.IsService(call.Sender))
            .GroupBy(call => call.UserId!)
            .ToDictionary(g => g.Key, g => g.MaxBy(call => call.Timestamp)!.Sender));

    // What a listed event shows: the account's name where a call carried one, else what the event
    // carries itself (its sender, or the id).
    public string Of(string? userId, string fallback) =>
        userId is not null && _names.TryGetValue(userId, out var name) ? name : fallback;
}