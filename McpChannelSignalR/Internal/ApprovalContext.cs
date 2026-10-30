using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpChannelSignalR.Internal;

// One question showing here: asked in a topic (TopicId set), or offered to a person wherever they are
// (TopicId only when a topic holds the conversation it is about). Its answer says whether it was
// given here or settled by another channel, since an offer answered elsewhere has no answer here.
internal sealed class ApprovalContext : IDisposable
{
    private readonly TaskCompletionSource<(ToolApprovalResult Result, bool Elsewhere)> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenRegistration _registration;

    public string? TopicId { get; init; }
    public string? SpaceSlug { get; init; }
    public required IReadOnlyList<ToolApprovalRequest> Requests { get; init; }
    public Account? Asker { get; init; }
    public bool Offered { get; init; }

    public bool TrySetResult(ToolApprovalResult result, bool elsewhere = false) =>
        _tcs.TrySetResult((result, elsewhere));

    public Task<(ToolApprovalResult Result, bool Elsewhere)> WaitForApprovalAsync(CancellationToken cancellationToken)
    {
        _registration = cancellationToken.Register(() => _tcs.TrySetCanceled(cancellationToken));
        return _tcs.Task;
    }

    public void Dispose()
    {
        _registration.Dispose();
    }
}