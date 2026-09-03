using System.ClientModel.Primitives;
using System.Net;
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// The SDK's stock policy retries a 429 three times with sub-second backoff, which is the right
// shape for a blip and the wrong one for a rate limit: a provider that said "slow down" is still
// saying it four seconds later. These pin the policy that replaces it on the OpenRouter pipeline.
public sealed class OpenRouterRetryPolicyTests
{
    [Fact]
    public async Task Send_RateLimitedMoreTimesThanTheSdkTolerates_StillSucceeds()
    {
        var transport = new ScriptedTransport(
            Enumerable.Repeat(RateLimited(), 5).Append(Ok()).ToArray());
        var policy = new RecordingPolicy();

        var message = await SendAsync(policy, transport);

        message.Response!.Status.ShouldBe(200);
        transport.Attempts.ShouldBe(6);
    }

    [Fact]
    public async Task Send_RateLimitedWithoutRetryAfter_BacksOffInSecondsNotMilliseconds()
    {
        var transport = new ScriptedTransport(
            Enumerable.Repeat(RateLimited(), 5).Append(Ok()).ToArray());
        var policy = new RecordingPolicy();

        await SendAsync(policy, transport);

        policy.Delays.Count.ShouldBe(5);
        policy.Delays[0].ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5));
        for (var i = 1; i < policy.Delays.Count; i++)
        {
            policy.Delays[i].ShouldBeGreaterThan(policy.Delays[i - 1]);
        }
        policy.Delays[^1].ShouldBeLessThanOrEqualTo(OpenRouterRetryPolicy.MaxRateLimitDelay);
    }

    [Fact]
    public async Task Send_RateLimitedWithRetryAfter_WaitsWhatTheProviderAsked()
    {
        var transport = new ScriptedTransport(RateLimited(retryAfterSeconds: 45), Ok());
        var policy = new RecordingPolicy();

        await SendAsync(policy, transport);

        policy.Delays.ShouldHaveSingleItem().ShouldBe(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task Send_RateLimitedForever_GivesUpAfterTheRateLimitBudget()
    {
        var transport = new ScriptedTransport(Enumerable.Repeat(RateLimited(), 20).ToArray());
        var policy = new RecordingPolicy();

        var message = await SendAsync(policy, transport);

        message.Response!.Status.ShouldBe(429);
        transport.Attempts.ShouldBe(OpenRouterRetryPolicy.RateLimitRetries + 1);
    }

    [Fact]
    public async Task Send_ServerError_KeepsTheSdkBudgetAndBackoff()
    {
        var transport = new ScriptedTransport(Enumerable.Repeat(ServerError(), 20).ToArray());
        var policy = new RecordingPolicy();

        var message = await SendAsync(policy, transport);

        message.Response!.Status.ShouldBe(503);
        transport.Attempts.ShouldBe(4);
        policy.Delays[0].ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Send_WithNoRetries_SendsOnce()
    {
        var transport = new ScriptedTransport(RateLimited(), Ok());
        var policy = new RecordingPolicy(maxRetries: 0);

        var message = await SendAsync(policy, transport);

        message.Response!.Status.ShouldBe(429);
        transport.Attempts.ShouldBe(1);
    }

    private static async Task<PipelineMessage> SendAsync(ClientRetryPolicy policy, HttpMessageHandler transport)
    {
        var options = new ClientPipelineOptions
        {
            RetryPolicy = policy,
            Transport = new HttpClientPipelineTransport(new HttpClient(transport))
        };
        var pipeline = ClientPipeline.Create(options);
        var message = pipeline.CreateMessage(new Uri("http://localhost/api/v1/responses"), "POST");
        await pipeline.SendAsync(message);
        return message;
    }

    private static Func<HttpResponseMessage> RateLimited(int? retryAfterSeconds = null) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"code":429,"message":"Rate limit exceeded"}}""")
        };
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(seconds));
        }

        return response;
    };

    private static Func<HttpResponseMessage> ServerError() =>
        () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("") };

    private static Func<HttpResponseMessage> Ok() =>
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

    private sealed class RecordingPolicy(int? maxRetries = null)
        : OpenRouterRetryPolicy(maxRetries ?? DefaultMaxRetries)
    {
        public List<TimeSpan> Delays { get; } = [];

        protected override Task WaitAsync(TimeSpan time, CancellationToken cancellationToken)
        {
            Delays.Add(time);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedTransport(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var next = script[Math.Min(Attempts, script.Length - 1)];
            Attempts++;
            return Task.FromResult(next());
        }
    }
}