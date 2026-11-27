using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// A provider now and then ends a reply with nothing in it: on prod, Pearl Research generated 91
// tokens of what was by every sign a tool call, and OpenRouter passed on none of them with finish
// "stop". After a tool result that ends the turn with no word, so the person is left asking whether
// it is done; asked once more, the model carries on.
public class OpenRouterChatClientEmptyReplyTests
{
    private static readonly ChatMessage[] AfterAToolResult =
    [
        new(ChatRole.User, "make the band another colour"),
        new(ChatRole.Assistant, [new FunctionCallContent("call-1", "domain__filesystem__text_edit", new Dictionary<string, object?>())]),
        new(ChatRole.Tool, [new FunctionResultContent("call-1", "{\"status\":\"success\"}")])
    ];

    private static ChatResponseUpdate Update(params AIContent[] contents) =>
        new(ChatRole.Assistant, contents) { ResponseId = "resp" };

    private static ChatResponseUpdate Finish(ChatFinishReason finish, params AIContent[] contents) =>
        new(ChatRole.Assistant, contents) { ResponseId = "resp", FinishReason = finish };

    private static readonly ChatResponseUpdate[] Empty = [Finish(ChatFinishReason.Stop)];

    private static readonly ChatResponseUpdate[] Call =
    [
        Update(new FunctionCallContent("call-2", "domain__filesystem__exec", new Dictionary<string, object?> { ["command"] = "./check" })),
        Finish(ChatFinishReason.ToolCalls)
    ];

    private static async Task<(ChatResponse Response, int Asked)> AskAsync(
        IEnumerable<ChatMessage> messages, int? maxRetries = null, IMetricsPublisher? metrics = null,
        params ChatResponseUpdate[][] rounds)
    {
        var model = new RoundsStream(rounds);
        var sut = new OpenRouterChatClient(
            model, "deepseek/deepseek-v4.1-flash-1017", metricsPublisher: metrics, maxRetries: maxRetries);
        var updates = await sut.GetStreamingResponseAsync(messages).ToListAsync();
        return (updates.ToChatResponse(), model.Asked);
    }

    [Fact]
    public async Task AnEmptyReplyAfterAToolResult_IsAskedAgain_AndWhatComesBackIsTheReply()
    {
        var (response, asked) = await AskAsync(AfterAToolResult, rounds: [Empty, Call]);

        asked.ShouldBe(2);
        response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .ShouldHaveSingleItem().Name.ShouldBe("domain__filesystem__exec");
    }

    // Thinking out loud is not an answer: the person still sees nothing after it.
    [Fact]
    public async Task AReplyThatIsOnlyReasoningOrBlankText_CountsAsEmpty()
    {
        var thoughtOnly = new[] { Update(new TextReasoningContent("Fine, re-run the check.")), Finish(ChatFinishReason.Stop, new TextContent("\n\n")) };

        var (response, asked) = await AskAsync(AfterAToolResult, rounds: [thoughtOnly, [Finish(ChatFinishReason.Stop, new TextContent("Done."))]]);

        asked.ShouldBe(2);
        response.Text.ShouldEndWith("Done.");
    }

    [Fact]
    public async Task AnEmptyReplyAskedAgain_IsAskedOnceMoreOnly()
    {
        var (_, asked) = await AskAsync(AfterAToolResult, rounds: [Empty, Empty, Call]);

        asked.ShouldBe(2);
    }

    // Only where the model owes the person a next step: after their own message an empty reply is
    // what it chose to say.
    [Fact]
    public async Task AnEmptyReplyToThePersonsMessage_IsNotAskedAgain()
    {
        var (_, asked) = await AskAsync([new ChatMessage(ChatRole.User, "ok")], rounds: [Empty, Call]);

        asked.ShouldBe(1);
    }

    [Fact]
    public async Task AReplyCutForLength_IsNotAskedAgain()
    {
        var (_, asked) = await AskAsync(AfterAToolResult, rounds: [[Finish(ChatFinishReason.Length)], Call]);

        asked.ShouldBe(1);
    }

    // The same rule as every other re-send: a host that must never see a turn twice (Lemonade) never does.
    [Fact]
    public async Task AHostThatRetriesNothing_IsNotAskedAgain()
    {
        var (_, asked) = await AskAsync(AfterAToolResult, maxRetries: 0, rounds: [Empty, Call]);

        asked.ShouldBe(1);
    }

    // Both asks were paid for, so both are on the bill.
    [Fact]
    public async Task EachAsk_IsBilledOnItsOwn()
    {
        var metrics = new RecordingMetrics();
        ChatResponseUpdate[] emptyBilled = [Finish(ChatFinishReason.Stop, new UsageContent(new UsageDetails { InputTokenCount = 67545, OutputTokenCount = 91 }))];
        ChatResponseUpdate[] callBilled = [.. Call, Update(new UsageContent(new UsageDetails { InputTokenCount = 67600, OutputTokenCount = 90 }))];

        await AskAsync(AfterAToolResult, metrics: metrics, rounds: [emptyBilled, callBilled]);

        metrics.Events.OfType<TokenUsageEvent>().Select(e => e.InputTokens).ShouldBe([67545, 67600]);
    }

    private sealed class RecordingMetrics : IMetricsPublisher
    {
        private readonly List<MetricEvent> _events = [];

        public IReadOnlyList<MetricEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Publish(MetricEvent metricEvent)
        {
            lock (_events)
            {
                _events.Add(metricEvent);
            }
        }
    }

    private sealed class RoundsStream(ChatResponseUpdate[][] rounds) : IChatClient
    {
        private int _asked;

        public int Asked => _asked;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var round = rounds[Interlocked.Increment(ref _asked) - 1];
            foreach (var update in round)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}