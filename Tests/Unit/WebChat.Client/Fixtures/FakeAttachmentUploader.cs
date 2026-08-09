using Domain.DTOs.Channel;
using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Fixtures;

// The upload store from the browser's side. Nothing here is about HTTP: what a test cares about
// is that a file was offered, how far it got, and what came back.
public sealed class FakeAttachmentUploader : IAttachmentUploader
{
    private readonly List<string> _uploaded = [];

    public IReadOnlyList<string> Uploaded => _uploaded;

    public string? LastTicket { get; private set; }

    public string? LastTopicId { get; private set; }

    // Held open so a test can watch a file that is still going, cancel it, or type while it runs.
    public TaskCompletionSource? Gate { get; set; }

    public string? RefuseWith { get; set; }

    public IReadOnlyList<int> ReportProgress { get; set; } = [];

    public async Task<UploadOutcome> UploadAsync(
        string topicId,
        string ticket,
        PickedFile file,
        Action<int> onProgress,
        CancellationToken ct)
    {
        LastTopicId = topicId;
        LastTicket = ticket;
        _uploaded.Add(file.FileName);

        foreach (var percent in ReportProgress)
        {
            onProgress(percent);
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        if (RefuseWith is not null)
        {
            return new UploadOutcome(null, RefuseWith);
        }

        return new UploadOutcome(
            new AttachmentReference
            {
                Id = $"7-42/{file.FileName}",
                FileName = file.FileName,
                MediaType = file.MediaType,
                SizeBytes = file.SizeBytes
            },
            null);
    }
}