using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Mvc;

namespace McpChannelSignalR.Attachments;

// One file per HTTP request, deliberately: the web host's default request body cap is below the
// combined size of a full message's attachments at the configured maximum, and the hub's own
// message size limit stays untouched because bytes never ride the hub.
public static class AttachmentEndpoints
{
    public const string TicketHeader = "X-Attachment-Ticket";
    public const string UploadPath = "/api/attachments";
    public const string DownloadPath = "/api/attachments";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(UploadPath, UploadAsync).DisableAntiforgery();
        app.MapGet($"{DownloadPath}/{{conversation}}/{{attachment}}", DownloadAsync);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        [FromQuery] string? topicId,
        AttachmentTickets tickets,
        AttachmentStore store,
        AttachmentSettings settings,
        CancellationToken ct)
    {
        var token = context.Request.Headers[TicketHeader].FirstOrDefault();
        var scope = tickets.ResolveUpload(token, topicId);
        if (scope is null)
        {
            return Results.Unauthorized();
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest("Send one file as a multipart form part named \"file\".");
        }

        var form = await context.Request.ReadFormAsync(ct);
        if (form.Files.Count != 1)
        {
            return Results.BadRequest("Send exactly one file per request.");
        }

        var file = form.Files[0];
        if (file.Length > settings.MaxBytesPerFile)
        {
            return Results.Text(
                $"{file.FileName} is {file.Length} bytes, above the {settings.MaxBytesPerFile} byte limit.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var mediaType = file.ContentType ?? string.Empty;
        if (!settings.AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            return Results.Text(
                $"{mediaType} is not a kind this chat accepts; attach an image or a PDF.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // Counted last, so a refused file does not spend one of the message's slots.
        if (!scope.TryTakeSlot(settings.MaxFilesPerMessage))
        {
            return Results.BadRequest(
                $"A message takes at most {settings.MaxFilesPerMessage} files.");
        }

        await using var content = file.OpenReadStream();
        var reference = await store.SaveAsync(
            scope.ConversationId, scope.SpaceSlug, file.FileName, mediaType, content, ct);
        return Results.Ok(reference);
    }

    private static IResult DownloadAsync(
        string conversation,
        string attachment,
        [FromQuery] string? ticket,
        AttachmentTickets tickets,
        AttachmentStore store)
    {
        var id = $"{conversation}/{attachment}";
        if (!tickets.ResolvesDownload(ticket, id))
        {
            return Results.Unauthorized();
        }

        var record = store.Find(id);
        return record is null
            ? Results.NotFound()
            : Results.File(record.Path, record.Reference.MediaType, record.Reference.FileName);
    }
}