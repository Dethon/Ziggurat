using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class AttachmentService(IChatLiveConnection liveConnection) : IAttachmentService
{
    public Task<HubResult<AttachmentLimits>> GetLimitsAsync() =>
        liveConnection.InvokeAsync<AttachmentLimits>("GetAttachmentLimits");

    public Task<HubResult<UploadTicket>> CreateUploadTicketAsync(string topicId) =>
        liveConnection.InvokeAsync<UploadTicket>("CreateUploadTicket", topicId);

    public Task<HubResult<AttachmentDownload>> CreateDownloadAsync(string attachmentId) =>
        liveConnection.InvokeAsync<AttachmentDownload>("CreateAttachmentDownload", attachmentId);
}