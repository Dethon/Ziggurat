using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Transcription;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Mvc;

namespace McpChannelSignalR.Dictation;

// One recording in, words out. Nothing is written to the upload store, no reference is minted, and
// the sweeper and retention rules never see it — a dictation is composer text, and the audio is
// gone the moment the transcript exists.
public static class DictationEndpoints
{
    // WebChat is deliberately not gated on the confidence floors — the person reads the words
    // before sending them — so the outcome here says the transcript reached a composer, not that
    // anything judged it.
    private const string Channel = VoiceChannels.Web;

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
        IMetricsPublisher metrics,
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

        // The part's own content type is whatever the browser felt like writing, so the bytes
        // decide — the same rule Telegram follows, for the same reason: whisper answers 400 to
        // every Opus container, and believing a claim is how one reaches it.
        var bytes = audio.ToArray();
        // NeedsDecoding is refused rather than decoded: the browser encodes WAV precisely because
        // whisper cannot read Opus, and this server has no decoder to make up for one that did not.
        if (AudioContainer.Sniff(bytes) is not { NeedsDecoding: false } container)
        {
            return Results.Text(
                "That recording is in a format I cannot read.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        var logger = loggers.CreateLogger(typeof(DictationEndpoints));
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await transcriber.TranscribeAsync(
                new TranscriptionRequest
                {
                    Audio = bytes,
                    MediaType = container.MediaType,
                    Language = settings.Transcription.Language
                },
                ct);

            clock.Stop();
            metrics.RecordTranscribed(Channel, "composed", result, clock.ElapsedMilliseconds);

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
            metrics.RecordFailure(Channel, ex);
            return Results.Text(
                "I could not turn that recording into words.", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}