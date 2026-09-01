using System.Net;
using System.Text;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// The stream a Lemonade chat host answered a one-word turn with, captured off a real box. The
// served model is a file path on that box's disk, which is why nothing downstream may use it.
internal sealed class LemonadeResponsesSseHandler : HttpMessageHandler
{
    private const string Sse =
        """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp_1","object":"response","status":"in_progress"}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","item":{"content":[],"id":"msg_1","role":"assistant","status":"in_progress","type":"message"}}

        event: response.content_part.added
        data: {"type":"response.content_part.added","item_id":"msg_1","part":{"type":"output_text","text":""}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_1","delta":"ok"}

        event: response.output_text.done
        data: {"type":"response.output_text.done","item_id":"msg_1","text":"ok"}

        event: response.content_part.done
        data: {"type":"response.content_part.done","item_id":"msg_1","part":{"type":"output_text","annotations":[],"logprobs":[],"text":"ok"}}

        event: response.output_item.done
        data: {"type":"response.output_item.done","item":{"type":"message","status":"completed","id":"msg_1","content":[{"type":"output_text","annotations":[],"logprobs":[],"text":"ok"}],"role":"assistant"}}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp_1","object":"response","created_at":1788300608,"status":"completed","model":"W:/AI/Lemonade/Models/models--unsloth--Qwen3.8-27B-GGUF\\snapshots\\4ca7\\Qwen3.8-27B-UD-Q4_K_XL.gguf","output":[{"type":"message","status":"completed","id":"msg_1","content":[{"type":"output_text","annotations":[],"logprobs":[],"text":"ok"}],"role":"assistant"}],"usage":{"input_tokens":18,"output_tokens":2,"total_tokens":20,"input_tokens_details":{"cached_tokens":0}}}}

        data: [DONE]

        """;

    public string? CapturedBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream")
        };
    }
}