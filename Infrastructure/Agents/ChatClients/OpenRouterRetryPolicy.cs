using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;

namespace Infrastructure.Agents.ChatClients;

// The SDK's stock policy treats a 429 like a 503: three tries, 0.8s/1.6s/3.2s apart, and a
// Retry-After honoured only when it exceeds that. A rate limit is not a blip — the provider is
// still saying "slow down" four seconds later, and a test suite (or a burst of scheduled agents)
// that hits one on every request pays a whole turn for it. This keeps the SDK's budget and
// backoff for everything else and gives a 429 its own: more tries, seconds apart, growing to a
// ceiling, and whatever the provider asked for when it said. A generation request has no side
// effect on the provider, so re-sending it is free of everything but time.
public class OpenRouterRetryPolicy : ClientRetryPolicy
{
    public const int DefaultMaxRetries = 3;
    public const int RateLimitRetries = 6;
    public static readonly TimeSpan InitialRateLimitDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxRateLimitDelay = TimeSpan.FromSeconds(30);

    private const double Jitter = 0.2;
    private readonly int _rateLimitRetries;

    // maxRetries is the budget for the statuses the SDK retries; zero disables every retry, the
    // rate-limited ones included, because a caller that asked for none must never see a turn
    // sent twice (the Lemonade host is that caller).
    public OpenRouterRetryPolicy(int maxRetries = DefaultMaxRetries) : base(maxRetries)
    {
        _rateLimitRetries = maxRetries == 0 ? 0 : Math.Max(maxRetries, RateLimitRetries);
    }

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
    {
        if (exception is null && IsRateLimited(message))
        {
            return RetriesSoFar(message) < _rateLimitRetries;
        }

        return base.ShouldRetry(message, exception);
    }

    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount)
    {
        if (!IsRateLimited(message))
        {
            return base.GetNextDelay(message, tryCount);
        }

        // The provider's own hint wins outright when it gave one — it knows when the window
        // resets and this policy does not. The backoff is for the 429 that came without one.
        if (TryGetRetryAfter(message.Response!, out var asked))
        {
            return asked;
        }

        var exponent = Math.Clamp(tryCount - 1, 0, 30);
        var backoff = InitialRateLimitDelay * Math.Pow(2, exponent);
        backoff *= 1 + (Random.Shared.NextDouble() * 2 - 1) * Jitter;
        return backoff > MaxRateLimitDelay ? MaxRateLimitDelay : backoff;
    }

    // The SDK keeps its own retry counter internal, so the rate-limit budget rides the message.
    protected override void OnTryComplete(PipelineMessage message)
    {
        message.SetProperty(typeof(OpenRouterRetryPolicy), RetriesSoFar(message) + 1);
        base.OnTryComplete(message);
    }

    private static bool IsRateLimited(PipelineMessage message) =>
        message.Response?.Status == (int)HttpStatusCode.TooManyRequests;

    private static int RetriesSoFar(PipelineMessage message) =>
        message.TryGetProperty(typeof(OpenRouterRetryPolicy), out var value) && value is int retries
            ? retries
            : 0;

    // Retry-After is seconds or an HTTP date; anything else is treated as absent rather than
    // trusted, because a malformed hint must not turn into a wait of zero or of a century.
    private static bool TryGetRetryAfter(PipelineResponse response, out TimeSpan value)
    {
        value = default;
        if (!response.Headers.TryGetValue("Retry-After", out var header) || header is null)
        {
            return false;
        }

        if (double.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0 && double.IsFinite(seconds))
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (DateTimeOffset.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var wait = at - DateTimeOffset.UtcNow;
            value = wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            return true;
        }

        return false;
    }
}