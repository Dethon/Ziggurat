using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents;

namespace Tests.Integration.Fixtures;

// Helpers for tests that drive a real LLM, where an occasional bad answer says nothing about the
// contract under test. One good answer is enough to prove a contract holds, so a positive
// assertion gets a few attempts. When a parser swallows a wrong-shaped answer, the empty result it
// returns looks exactly like a legitimately empty one, so the warnings it logged carry the reason
// and belong in the failure message.
public static class LlmAttempt
{
    private const int MaxAttempts = 3;
    private const int MaxStallRetries = 2;

    // What one turn against a real provider is given before it counts as stalled. Call sites with
    // a heavier turn pass their own.
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(120);

    public static async Task<T> UntilAsync<T>(Func<Task<T>> call, Func<T, bool> usable)
    {
        var result = await call();
        for (var attempt = 1; attempt < MaxAttempts && !usable(result); attempt++)
        {
            result = await call();
        }
        return result;
    }

    // A provider that stops sending bytes mid-stream burns the whole budget and hands the test a
    // cancellation with nothing to assert on — that says as little about the contract as a bad
    // answer does, so the attempt runs again on a fresh budget. Each attempt gets its own
    // deadline rather than sharing one, because a stall that consumed the first deadline would
    // otherwise leave the retry no time to answer. Only the stall is retried: an assertion that
    // failed inside the attempt is a real failure and propagates on the first try.
    public static async Task<T> WithinAsync<T>(TimeSpan budget, Func<CancellationToken, Task<T>> call)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var cts = new CancellationTokenSource(budget);
            try
            {
                return await call(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && attempt < MaxStallRetries)
            {
                // Next attempt.
            }
        }
    }

    // One agent turn, run until it lands, with both retries in one place: each attempt gets its own
    // budget so a stall cannot spend the next attempt's, and a turn where the model replied without
    // doing what the test is about runs again. The agent is built per attempt rather than passed in,
    // because a stalled turn leaves half of itself in the thread store and a retry on that same
    // agent would send the model a conversation that never happened.
    public static Task<List<AiResponse>> TurnAsync(
        Func<McpAgent> createAgent,
        string prompt,
        Func<IReadOnlyList<AiResponse>, bool> landed,
        TimeSpan? budget = null) =>
        UntilAsync(
            () => WithinAsync(budget ?? Budget, async ct =>
            {
                await using var agent = createAgent();
                return await agent.RunStreamingAsync(prompt, cancellationToken: ct)
                    .ToUpdateAiResponsePairs()
                    .Where(x => x.Item2 is not null)
                    .Select(x => x.Item2!)
                    .ToListAsync(ct);
            }),
            landed);

    // Tool calls as well as text: what the model did is as much the answer as what it said, and a
    // test that only reads Content cannot tell a refused turn from one that never called the tool.
    public static string Combine(IEnumerable<AiResponse> responses) =>
        string.Join(" ", responses.Select(r => r.Content + " " + r.ToolCalls));

    public static string Explain(string what, IReadOnlyCollection<string> warnings) =>
        warnings.Count == 0
            ? $"{what} (no parse warnings logged, so the model answered with a valid but empty response)"
            : $"{what} (parse warnings: {string.Join(" | ", warnings)})";
}