using System.Net;
using System.Text;

namespace Tests.Unit.Infrastructure;

// Terminal HttpMessageHandler for driving a real OpenRouterChatClient pipeline offline:
// captures the outgoing request and answers with a minimal valid SSE completion.
internal sealed class CapturingSseHandler : HttpMessageHandler
{
    private const string Sse =
        """
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}

        data: [DONE]

        """;

    public string? CapturedBody { get; private set; }
    public Uri? CapturedUri { get; private set; }
    public string? CapturedAuthorization { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CapturedUri = request.RequestUri;
        CapturedAuthorization = request.Headers.Authorization?.ToString();
        CapturedBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream")
        };
    }
}