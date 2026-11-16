using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Security;
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
        app.MapGet($"{AttachmentEndpointPaths.Attachments}/{{conversation}}/{{attachment}}", DownloadAsync);
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
            scope.Conversation, scope.SpaceSlug, file.FileName, file.ContentType ?? string.Empty, content, ct);
        return Results.Ok(reference);
    }

    // The same rules the composer applies at pick time, from the same source, because they are
    // the same rules — the composer only gets to say them sooner. Only the status code is this
    // endpoint's own.
    private static IResult? Refuse(IFormFile file, AttachmentSettings settings) =>
        AttachmentRefusals.Refusal(
            file.FileName, file.ContentType ?? string.Empty, file.Length, settings.Limits) switch
        {
            null => null,
            { Kind: AttachmentRefusalKind.TooLarge } refusal => Results.Text(
                refusal.Message, statusCode: StatusCodes.Status413PayloadTooLarge),
            var refusal => Results.Text(
                refusal.Message, statusCode: StatusCodes.Status415UnsupportedMediaType)
        };

    // The ticket names the person it was minted for, and only that person, as the proxy's session
    // check signed them for the chat channel, gets the file: a copied link opens nothing for anyone else.
    private static async Task<IResult> DownloadAsync(
        HttpContext context,
        string conversation,
        string attachment,
        [FromQuery] string? ticket,
        AttachmentTickets tickets,
        AttachmentStore store,
        AssertionVerifier assertions)
    {
        var id = $"{conversation}/{attachment}";
        if (tickets.DownloaderOf(ticket, id) is not { } downloader)
        {
            return Results.Unauthorized();
        }

        var admission = await assertions.AdmitAsync(
            context.Request.Headers, ChannelIds.SignalR, null, context.RequestAborted);
        if (admission is not AppAdmission.Admitted { Person.UserId: var asker })
        {
            return Results.Unauthorized();
        }

        if (asker != downloader)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Ranges, because a <video> element asks for them to start playing and to seek; the ticket
        // is not spent by a request, so every range a clip's playback asks for is answered.
        var record = store.Find(id);
        return record is null
            ? Results.NotFound()
            : Results.File(
                record.Path, record.Reference.MediaType, record.Reference.FileName, enableRangeProcessing: true);
    }
}