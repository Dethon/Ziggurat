using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class AttachmentService(
    IChatLiveConnection liveConnection,
    AttachmentEndpointResolver endpoints) : IAttachmentService
{
    public Task<HubResult<AttachmentLimits>> GetLimitsAsync() =>
        liveConnection.InvokeAsync<AttachmentLimits>("GetAttachmentLimits");

    public Task<HubResult<UploadTicket>> CreateUploadTicketAsync(string topicId) =>
        liveConnection.InvokeAsync<UploadTicket>("CreateUploadTicket", topicId);

    // The hub answers with a server-relative path because it does not know how this browser
    // reaches it. Resolving here is what makes the URL usable as an <img src> as well.
    public async Task<HubResult<AttachmentDownload>> CreateDownloadAsync(string attachmentId)
    {
        var minted = await liveConnection.InvokeAsync<AttachmentDownload>(
            "CreateAttachmentDownload", attachmentId);
        if (minted is not { IsLive: true, Value: not null })
        {
            return minted;
        }

        var absolute = await endpoints.ResolveAsync(minted.Value.Url);
        return HubResult<AttachmentDownload>.Answered(minted.Value with { Url = absolute });
    }
}