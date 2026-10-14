using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Tools.Sugary.Estimation;
using McpServerSugary.Channel;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace McpServerSugary.App;

// The app's estimate, answered by a turn of the sugary agent. The request is the standalone app's
// form and the answer its estimate, unchanged, so the app did not have to change with it. Each
// estimate is a conversation of its own for now; follow-up answers on the same conversation are a
// later ticket (06).
public static class EstimateEndpoints
{
    public static void MapEstimateEndpoints(this IEndpointRouteBuilder api)
    {
        // The session cookie is SameSite=Strict, so a cross-site form post never carries it and an
        // antiforgery token would add nothing.
        api.MapPost("/estimates", EstimateAsync).DisableAntiforgery();
    }

    // The form is read here rather than bound, so the route matches whatever a caller sends and the
    // cookie gate answers first: a bound form would make a JSON post not match at all, and a phone
    // that has not signed in would be told the route does not exist.
    private static async Task<IResult> EstimateAsync(
        HttpRequest request, SugaryChannel channel, UploadStore uploads, IAgentCatalog catalog,
        SugarySettings settings, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return Problem(StatusCodes.Status400BadRequest, "Send the meal as a form.");
        }

        var form = await request.ReadFormAsync(ct);
        string? Field(string name) => form.TryGetValue(name, out var value) ? value.ToString() : null;
        var description = Field("description");
        var photos = form.Files.GetFiles("photos");

        if (photos.Count == 0 && string.IsNullOrWhiteSpace(description))
        {
            return Problem(StatusCodes.Status400BadRequest, "Add a photo or describe the meal.");
        }

        // Refused before anything is kept or sent, so a photo the model would choke on costs neither
        // a turn nor a file, and a model that cannot see photos never answers as if none was sent.
        if ((PhotoUpload.Refusal(photos, settings.Uploads)
             ?? PhotoUpload.ModelRefusal(photos, catalog.Get(settings.Agent))) is { } refusal)
        {
            return Problem(StatusCodes.Status400BadRequest, refusal);
        }

        // The photos cross to the agent as references to this store, never as bytes on the turn, so
        // the conversation's history stays the size of its words (ADR-0020).
        var conversationId = Guid.NewGuid().ToString("N");
        var attachments = await StoreAsync(conversationId, photos, uploads, ct);
        var turn = MealTurn.Text(new MealInput(
            attachments.Count, description, Field("clarifications"), Field("language"), Field("counted")));
        var reply = await channel.AskAsync(conversationId, turn, MealEstimateSchema.Format, attachments, ct);

        // Every way of not getting an estimate is said, and none of them is shown as one. A 503
        // would read in the app as "the model is not set up", so a missing agent is a bad gateway.
        return reply switch
        {
            TurnReply.Answered answered => Read(answered.Text),
            TurnReply.Failed failed => Problem(StatusCodes.Status502BadGateway, failed.Reason),
            TurnReply.Unanswered => Problem(StatusCodes.Status502BadGateway, "The model returned no answer."),
            TurnReply.NotListening => Problem(StatusCodes.Status502BadGateway,
                "The estimating agent is not connected right now. Try again in a moment, or count this meal by hand."),
            TurnReply.TimedOut => Problem(StatusCodes.Status504GatewayTimeout,
                "The estimate took too long. Try again, or count this meal by hand."),
            _ => throw new InvalidOperationException($"No answer for a turn that ended as {reply}.")
        };
    }

    // One at a time and in the order she took them, so the references keep the order of the photos.
    private static async Task<IReadOnlyList<AttachmentReference>> StoreAsync(
        string conversationId, IReadOnlyList<IFormFile> photos, UploadStore uploads, CancellationToken ct)
    {
        var stored = new List<AttachmentReference>(photos.Count);
        foreach (var photo in photos)
        {
            using var buffer = new MemoryStream();
            await photo.CopyToAsync(buffer, ct);
            stored.Add(await uploads.SaveAsync(conversationId, photo.FileName, photo.ContentType, buffer.ToArray(), ct));
        }

        return stored;
    }

    private static IResult Read(string reply)
    {
        try
        {
            return Results.Ok(MealEstimateReply.Read(reply));
        }
        catch (UnreadableEstimateException e)
        {
            return Problem(StatusCodes.Status502BadGateway, e.Message);
        }
    }

    private static IResult Problem(int status, string detail) => Results.Problem(detail, statusCode: status);
}