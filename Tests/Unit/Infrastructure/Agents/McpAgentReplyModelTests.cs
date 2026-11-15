using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// A reply keeps the model that wrote it — the turn's, as the whitelist decided it — so a reroll on
// another model can be told apart from the reply it stands beside.
public class McpAgentReplyModelTests
{
    [Theory]
    [InlineData(null, "test-model")]
    [InlineData("other/model", "other/model")]
    [InlineData("not/allowed", "test-model")]
    public async Task AReply_KeepsTheModelThatAnsweredIt(string? patched, string answeredBy)
    {
        var (store, persisted) = InMemoryStore();
        var agent = new McpAgent(
            TestAgentSpec.Default with { PatchableModels = new FixedPatchableModelSource(["other/model"]) },
            new AnsweringClient(), store, NoOpMetricsPublisher.Instance, TimeProvider.System, [], []);
        var message = new ChatMessage(ChatRole.User, "hola");
        message.SetConfigPatch(patched is null ? null : new AgentConfigPatch { Model = patched });

        await agent.RunAsync([message]);

        persisted.Values.Single().Last().GetModel().ShouldBe(answeredBy);
    }

    private static (IThreadStateStore Store, Dictionary<string, List<ChatMessage>> Persisted) InMemoryStore()
    {
        var persisted = new Dictionary<string, List<ChatMessage>>();
        var store = new Mock<IThreadStateStore>();
        store.Setup(s => s.GetMessagesAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => persisted.GetValueOrDefault(key)?.ToArray());
        store.Setup(s => s.AppendMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Returns((string key, IReadOnlyList<ChatMessage> messages) =>
            {
                if (!persisted.TryGetValue(key, out var list))
                {
                    persisted[key] = list = [];
                }

                list.AddRange(messages);
                return Task.CompletedTask;
            });
        return (store.Object, persisted);
    }

    private sealed class AnsweringClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}