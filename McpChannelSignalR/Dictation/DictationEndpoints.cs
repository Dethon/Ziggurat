using Domain.Contracts;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Mvc;

namespace McpChannelSignalR.Dictation;

// One recording in, words out. Nothing is written to the upload store, no reference is minted, and
// the sweeper and retention rules never see it — a dictation is composer text, and the audio is
// gone the moment the transcript exists.
public static class DictationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(DictationEndpointPaths.Transcriptions, TranscribeAsync).DisableAntiforgery();
    }

    private static async Task<IResult> TranscribeAsync(
        HttpContext context,
        [FromQuery(Name = DictationEndpointPaths.SpaceQueryParameter)] string? space,
        AttachmentTickets tickets,
        DictationSettings settings,
        IAudioTranscriber transcriber,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var token = context.Request.Headers[DictationEndpointPaths.TicketHeader].FirstOrDefault();
        if (!tickets.ResolvesDictation(token, space))
        {
            return Results.Unauthorized();
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest("Send one recording as a multipart form part named \"file\".");
        }

        var form = await context.Request.ReadFormAsync(ct);
        if (form.Files.Count != 1)
        {
            return Results.BadRequest("Send exactly one recording per request.");
        }

        var file = form.Files[0];
        if (file.Length > settings.MaxBytes)
        {
            return Results.Text(
                $"That recording is longer than the {settings.MaxLength.TotalMinutes:0.#} minutes I can take.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        using var audio = new MemoryStream((int)file.Length);
        await using (var content = file.OpenReadStream())
        {
            await content.CopyToAsync(audio, ct);
        }

        var logger = loggers.CreateLogger(typeof(DictationEndpoints));
        try
        {
            var result = await transcriber.TranscribeAsync(
                new TranscriptionRequest
                {
                    Audio = audio.ToArray(),
                    MediaType = file.ContentType is { Length: > 0 } declared ? declared : "audio/wav",
                    Language = settings.Transcription.Language
                },
                ct);

            return Results.Ok(new DictationTranscript(result.Text.Trim()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The recording is already gone, so the only thing left is to say so plainly: the
            // browser turns any non-2xx into its one-line composer refusal, and the way on is to
            // record again.
            logger.LogWarning(ex, "Could not transcribe a dictation: {Message}", ex.Message);
            return Results.Text(
                "I could not turn that recording into words.", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}