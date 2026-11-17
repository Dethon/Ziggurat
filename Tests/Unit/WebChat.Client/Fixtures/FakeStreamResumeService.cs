using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeStreamResumeService : IStreamResumeService
{
    private readonly TaskCompletionSource _gate = new();
    private readonly List<string> _resumed = [];
    private readonly Lock _lock = new();

    public bool BlockUntilReleased { get; set; }

    public IReadOnlyList<string> ResumedTopicIds
    {
        get
        {
            lock (_lock)
            {
                return [.. _resumed];
            }
        }
    }

    public void Release() => _gate.TrySetResult();

    public async Task TryResumeStreamAsync(StoredTopic topic, bool toldOfATurn = false)
    {
        lock (_lock)
        {
            _resumed.Add(topic.TopicId);
        }

        if (BlockUntilReleased)
        {
            await _gate.Task;
        }
    }
}