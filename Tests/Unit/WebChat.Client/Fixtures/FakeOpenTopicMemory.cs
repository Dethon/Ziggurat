using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeOpenTopicMemory : IOpenTopicMemory
{
    public Dictionary<string, string> Remembered { get; } = [];

    public ValueTask<string?> RecallAsync(string slug) => ValueTask.FromResult(Remembered.GetValueOrDefault(slug));

    public ValueTask RememberAsync(string slug, string? topicId)
    {
        if (topicId is null)
        {
            Remembered.Remove(slug);
        }
        else
        {
            Remembered[slug] = topicId;
        }

        return ValueTask.CompletedTask;
    }
}