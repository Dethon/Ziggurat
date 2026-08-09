using Domain.DTOs.WebChat;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Mvc;

namespace McpChannelSignalR.Attachments;

// One file per HTTP request, deliberately: the web host's default request body cap is below the
// combined size of a full message's attachments at the configured maximum, and the hub's own
// message size limit stays untouched because bytes never ride the hub.
public static class AttachmentEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(AttachmentEndpointPaths.Attachments, UploadAsync).DisableAntiforgery();
        app.MapGet($"{AttachmentEndpointPaths.Attachments}/{{conversation}}/{{attachment}}", Download);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        [FromQuery] string? topicId,
        AttachmentTickets tickets,
        AttachmentStore store,
        AttachmentSettings settings,
        CancellationToken ct)
    {
        var token = context.Request.Headers[AttachmentEndpointPaths.TicketHeader].FirstOrDefault();
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
        if (Refuse(file, settings) is { } refusal)
        {
            return refusal;
        }

        // Counted last, so a refused file does not spend one of the message's slots.
        if (!scope.TryTakeSlot(settings.MaxFilesPerMessage))
        {
            return Results.BadRequest(AttachmentRefusals.TooManyFiles(settings.MaxFilesPerMessage));
        }

        await using var content = file.OpenReadStream();
        var reference = await store.SaveAsync(
            scope.ConversationId, scope.SpaceSlug, file.FileName, file.ContentType ?? string.Empty, content, ct);
        return Results.Ok(reference);
    }

    // The same rules the composer applies at pick time, from the same wording, because they are
    // the same rules — the composer only gets to say them sooner.
    private static IResult? Refuse(IFormFile file, AttachmentSettings settings)
    {
        if (file.Length > settings.MaxBytesPerFile)
        {
            return Results.Text(
                AttachmentRefusals.TooLarge(file.FileName, settings.MaxBytesPerFile),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var mediaType = file.ContentType ?? string.Empty;
        return settings.AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase)
            ? null
            : Results.Text(
                AttachmentRefusals.UnsupportedKind(mediaType),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    private static IResult Download(
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