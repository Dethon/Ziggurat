using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// The Responses adapter leaves MessageId empty on every update, where the chat wire stamped the
// completion id. Everything downstream that reassembles a streamed turn — the update-to-response
// pairing, the monitor's streaming — keys on MessageId, so the client fills it from the response
// id: one id per model turn, which is exactly what the pairing wants.
public class OpenRouterChatClientMessageIdTests
{
    [Fact]
    public async Task AnUpdateWithoutAMessageId_GetsTheResponseIdAsOne()
    {
        var inner = new Mock<IChatClient>();
        inner
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Updates());
        var sut = new OpenRouterChatClient(inner.Object, "test-model");

        var seen = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            seen.Add(update);
        }

        seen.Count.ShouldBe(3);
        seen[0].MessageId.ShouldBe("gen-1");
        seen[1].MessageId.ShouldBe("gen-1");
        // An id the adapter did stamp is kept: the fallback fills a gap, never overwrites.
        seen[2].MessageId.ShouldBe("msg-own");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates()
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "hel") { ResponseId = "gen-1" };
        yield return new ChatResponseUpdate(ChatRole.Assistant, "lo") { ResponseId = "gen-1" };
        yield return new ChatResponseUpdate(ChatRole.Assistant, "!") { ResponseId = "gen-1", MessageId = "msg-own" };
    }
}