using System.Runtime.CompilerServices;
using System.Text.Json;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// DeepSeek now and then writes a tool call as text in its own markup instead of calling: on prod
// through Baidu and Novita, in the eval through Wafer and Together, so no provider pin avoids it.
// Left as text it ends the turn with the markup shown; read back as the call it is, the turn goes
// on as if the model had called properly.
public class OpenRouterChatClientDsmlTests
{
    private const string Exec =
        "<｜DSML｜ calls>\n<｜DSML｜ invoke name=\"domain__filesystem__exec\">\n"
        + "<｜DSML｜ parameter name=\"command\" string=\"true\">cd /apps/void-invaders && ./check 2>&1 | tail -30</｜DSML｜ parameter>\n"
        + "<｜DSML｜ parameter name=\"path\" string=\"true\">/apps/void-invaders</｜DSML｜ parameter>\n"
        + "<｜DSML｜ parameter name=\"timeoutSeconds\" string=\"false\">200</｜DSML｜ parameter>\n"
        + "</｜DSML｜ invoke>\n</｜DSML｜ calls>";

    private static async Task<List<ChatResponseUpdate>> StreamAsync(params ChatResponseUpdate[] updates)
    {
        var sut = new OpenRouterChatClient(new ScriptedStream(updates), "deepseek/deepseek-v4.1-flash-1017");
        return await sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")]).ToListAsync();
    }

    private static ChatResponseUpdate Text(string text, ChatFinishReason? finish = null) =>
        new(ChatRole.Assistant, text) { ResponseId = "resp-1", FinishReason = finish };

    // Every way the stream may cut the markup, a cut inside the opening marker included.
    private static ChatResponseUpdate[] Chunked(string text, int size) =>
        [.. text.Chunk(size).Select(c => Text(new string(c)))];

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    public async Task AReplyThatIsOnlyACallWrittenAsMarkup_IsTheCall(int chunk)
    {
        var updates = await StreamAsync([.. Chunked(Exec, chunk), Text("", ChatFinishReason.Stop)]);

        var response = updates.ToChatResponse();
        response.Text.ShouldBeEmpty();
        response.FinishReason.ShouldBe(ChatFinishReason.ToolCalls);
        var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ShouldHaveSingleItem();
        call.Name.ShouldBe("domain__filesystem__exec");
        call.CallId.ShouldNotBeNullOrWhiteSpace();
        call.Arguments!["command"].ShouldBe("cd /apps/void-invaders && ./check 2>&1 | tail -30");
        call.Arguments["path"].ShouldBe("/apps/void-invaders");
        ((JsonElement)call.Arguments["timeoutSeconds"]!).GetInt32().ShouldBe(200);
    }

    [Fact]
    public async Task WordsBeforeTheMarkup_AreKept_AndStreamedAsTheyCome()
    {
        var updates = await StreamAsync(Text("Let me check it. "), Text(Exec), Text("", ChatFinishReason.Stop));

        updates[0].Text.ShouldBe("Let me check it. ");
        var response = updates.ToChatResponse();
        response.Text.ShouldBe("Let me check it. ");
        response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SeveralInvokes_AreSeveralCalls_InOrder()
    {
        const string two =
            "<｜DSML｜ calls>\n<｜DSML｜ invoke name=\"web_action\">\n"
            + "<｜DSML｜ parameter name=\"ref\" string=\"true\">e-7</｜DSML｜ parameter>\n</｜DSML｜ invoke>\n"
            + "<｜DSML｜ invoke name=\"web_snapshot\">\n</｜DSML｜ invoke>\n</｜DSML｜ calls>";

        var updates = await StreamAsync(Text(two));

        var calls = updates.ToChatResponse().Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        calls.Select(c => c.Name).ShouldBe(["web_action", "web_snapshot"]);
        calls.Select(c => c.CallId).Distinct().Count().ShouldBe(2);
        calls[1].Arguments.ShouldBeEmpty();
    }

    // The spelling DeepSeek's own documentation gives, with no space after the bar.
    [Fact]
    public async Task TheDocumentedSpelling_IsReadToo()
    {
        const string documented =
            "<｜DSML｜function_calls>\n<｜DSML｜invoke name=\"get_weather\">\n"
            + "<｜DSML｜parameter name=\"city\" string=\"true\">Hangzhou</｜DSML｜parameter>\n"
            + "</｜DSML｜invoke>\n</｜DSML｜function_calls>";

        var updates = await StreamAsync(Text(documented));

        var call = updates.ToChatResponse().Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ShouldHaveSingleItem();
        call.Name.ShouldBe("get_weather");
        call.Arguments!["city"].ShouldBe("Hangzhou");
    }

    // Anything that is not a whole calls block is the model's words, shown as they were.
    [Theory]
    [InlineData("<｜DSML｜ calls>\n<｜DSML｜ invoke name=\"x\">\n")]
    [InlineData("<｜DSML｜ calls>\n</｜DSML｜ calls> and then more words")]
    [InlineData("A fullwidth bar ｜ and a <｜ that leads nowhere.")]
    public async Task TextThatIsNotAWholeCallBlock_IsLeftAsText(string text)
    {
        var updates = await StreamAsync([.. Chunked(text, 4), Text("", ChatFinishReason.Stop)]);

        var response = updates.ToChatResponse();
        response.Text.ShouldBe(text);
        response.FinishReason.ShouldBe(ChatFinishReason.Stop);
        response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task WhatElseTheStreamCarried_IsKept()
    {
        var usage = new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 10 })])
        {
            ResponseId = "resp-1",
            FinishReason = ChatFinishReason.Stop
        };

        var updates = await StreamAsync(Text(Exec), usage);

        updates.SelectMany(u => u.Contents).OfType<UsageContent>().ShouldHaveSingleItem().Details.InputTokenCount.ShouldBe(10);
    }

    // The point of it: the call runs and its result goes back to the model, which answers.
    [Fact]
    public async Task TheCallReadBack_IsRun_AndTheTurnGoesOn()
    {
        string? ran = null;
        var exec = AIFunctionFactory.Create(
            (string command, string path, int timeoutSeconds) => ran = $"{command} in {path} for {timeoutSeconds}s",
            "domain__filesystem__exec");
        var rounds = new Queue<ChatResponseUpdate[]>([[Text(Exec), Text("", ChatFinishReason.Stop)], [Text("Checked.", ChatFinishReason.Stop)]]);
        var model = new OpenRouterChatClient(new RoundsStream(rounds), "deepseek/deepseek-v4.1-flash-1017");
        using var turn = new FunctionInvokingChatClient(model);

        var updates = await turn.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [exec] }).ToListAsync();

        ran.ShouldBe("cd /apps/void-invaders && ./check 2>&1 | tail -30 in /apps/void-invaders for 200s");
        updates.ToChatResponse().Text.ShouldBe("Checked.");
    }

    private sealed class RoundsStream(Queue<ChatResponseUpdate[]> rounds) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            new ScriptedStream(rounds.Dequeue()).GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class ScriptedStream(ChatResponseUpdate[] updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}