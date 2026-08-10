using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

// What the live connection can be asked about attachments. Bytes never ride the hub: this hands
// out permission and the upload store does the carrying.
public interface IAttachmentService
{
    Task<HubResult<AttachmentLimits>> GetLimitsAsync();

    Task<HubResult<UploadTicket>> CreateUploadTicketAsync(string topicId);

    Task<HubResult<AttachmentDownload>> CreateDownloadAsync(string attachmentId);
}

// One file over HTTP, one request. Progress is reported as the bytes go, so a large file visibly
// moves; null is the upload that could not be made and the reason is the caller's to show.
public interface IAttachmentUploader
{
    Task<UploadOutcome> UploadAsync(
        string topicId,
        string ticket,
        PickedFile file,
        Action<int> onProgress,
        CancellationToken ct);
}

public sealed record UploadOutcome(AttachmentReference? Reference, string? Error);