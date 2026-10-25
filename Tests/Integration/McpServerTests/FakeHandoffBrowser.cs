using System.Collections.Concurrent;
using Domain.Contracts;

namespace Tests.Integration.McpServerTests;

// Handoff tabs with no browser behind them: which were opened, for whom, at what url, and which
// are still open. A test closes one itself to stage the browser restarting under it.
internal sealed class FakeHandoffBrowser : IHandoffBrowser
{
    public sealed record Opened(string Id, string Person, string Url);

    private readonly ConcurrentDictionary<string, Opened> _open = new();
    private readonly ConcurrentQueue<Opened> _opened = new();

    public IReadOnlyList<Opened> OpenedTabs => [.. _opened];

    public ConcurrentQueue<HandoffInput> Inputs { get; } = new();

    public IReadOnlyList<string> Hosts { get; set; } = [];

    public Task<string> OpenHandoffAsync(string person, string url, CancellationToken ct = default)
    {
        var opened = new Opened(Guid.NewGuid().ToString("N"), person, url);
        _open[opened.Id] = opened;
        _opened.Enqueue(opened);
        return Task.FromResult(opened.Id);
    }

    public bool IsHandoffOpen(string handoffId) => _open.ContainsKey(handoffId);

    public Task CloseHandoffAsync(string handoffId)
    {
        _open.TryRemove(handoffId, out _);
        return Task.CompletedTask;
    }

    public async Task<IAsyncDisposable> StreamHandoffAsync(
        string handoffId, Func<HandoffFrame, Task> onFrame, CancellationToken ct = default)
    {
        await onFrame(new HandoffFrame([0xFF, 0xD8, 0xFF], 1920, 1080));
        return new Nothing();
    }

    public Task SendHandoffInputAsync(string handoffId, HandoffInput input, CancellationToken ct = default)
    {
        Inputs.Enqueue(input);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> HandoffVisitedHosts(string handoffId) => Hosts;

    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}