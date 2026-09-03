using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// Proves the rate-limit policy is the one on the real pipeline, not just a class that exists:
// four 429s in a row exhaust the SDK's stock budget, so this passes only through the replacement.
public sealed class OpenRouterChatClientRateLimitTests
{
    [Fact]
    public async Task GetResponse_RateLimitedFourTimesThenServed_ReturnsTheReply()
    {
        var handler = new RateLimitThenSseHandler(rateLimitedResponses: 4);
        using var client = new OpenRouterChatClient(
            "http://localhost/api/v1", "test-key", "configured/model", transportHandler: handler);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Text.ShouldBe("ok");
        handler.Attempts.ShouldBe(5);
    }

    private sealed class RateLimitThenSseHandler(int rateLimitedResponses) : HttpMessageHandler
    {
        private const string Sse =
            """
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_1","object":"response","status":"in_progress"}}

            event: response.output_item.added
            data: {"type":"response.output_item.added","item":{"content":[],"id":"msg_1","role":"assistant","status":"in_progress","type":"message"}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","item_id":"msg_1","delta":"ok"}

            event: response.output_item.done
            data: {"type":"response.output_item.done","item":{"type":"message","status":"completed","id":"msg_1","content":[{"type":"output_text","annotations":[],"logprobs":[],"text":"ok"}],"role":"assistant"}}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_1","object":"response","status":"completed","model":"test-model","output":[{"type":"message","status":"completed","id":"msg_1","content":[{"type":"output_text","annotations":[],"logprobs":[],"text":"ok"}],"role":"assistant"}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}

            data: [DONE]

            """;

        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts <= rateLimitedResponses)
            {
                // Retry-After: 0 keeps the test instant while exercising the provider-hint path.
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("""{"error":{"code":429,"message":"Rate limit exceeded"}}""")
                };
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream")
            });
        }
    }
}