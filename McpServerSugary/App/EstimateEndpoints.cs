using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Tools.Sugary.Estimation;
using McpServerSugary.Channel;
using McpServerSugary.Clips;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.App;

// The app's estimate, answered by a turn of the sugary agent. The request is the standalone app's
// form and the answer its estimate, each with one field more: the conversation the first estimate
// opened, which the app sends back with her answers so they are the next turn of it.
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
        HttpRequest request, SugaryChannel channel, UploadStore uploads, OpenEstimates estimates,
        IAgentCatalog catalog, SugarySettings settings, ClipProcessor clips, ILogger<ClipProcessor> logger,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return Problem(StatusCodes.Status400BadRequest, "Send the meal as a form.");
        }

        // A phone's clip is larger than a web server takes in one request unless told, so this one
        // route is told how large a meal may be; the clip's own ceiling is checked once it is read.
        var limit = ClipUpload.MaxRequestBytes(settings.Uploads);
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
        {
            body.MaxRequestBodySize = limit;
        }

        var form = await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = limit }, ct);
        string? field(string name) => form.TryGetValue(name, out var value) ? value.ToString() : null;
        var photos = form.Files.GetFiles("photos");
        var filmed = form.Files.GetFiles("clip");

        if (field("conversationId") is { Length: > 0 } conversation)
        {
            return await FollowUpAsync(conversation, photos, filmed, field, channel, estimates, Log(loggers), ct);
        }

        var description = field("description");
        if (photos.Count == 0 && filmed.Count == 0 && string.IsNullOrWhiteSpace(description))
        {
            return Problem(StatusCodes.Status400BadRequest, "Add a photo or a clip, or describe the meal.");
        }

        // Refused before anything is kept or sent, so a photo the model would choke on costs neither
        // a turn nor a file, and a model that cannot see photos never answers as if none was sent.
        if ((PhotoUpload.Refusal(photos, settings.Uploads)
             ?? ClipUpload.Refusal(filmed, settings.Uploads)
             ?? PhotoUpload.ModelRefusal(photos, filmed.Count > 0, catalog.Get(settings.Agent))) is { } refusal)
        {
            return Problem(StatusCodes.Status400BadRequest, refusal);
        }

        // The clip is read and made small before anything is kept, so a clip too long or not a clip
        // at all is refused with nothing stored and nothing sent, like a photo.
        PreparedClip.Ready? clip = null;
        if (filmed.Count > 0)
        {
            try
            {
                switch (await clips.PrepareAsync(filmed[0], ct))
                {
                    case PreparedClip.Refused refused:
                        return Problem(StatusCodes.Status400BadRequest, refused.Reason);
                    case PreparedClip.Ready ready:
                        clip = ready;
                        break;
                }
            }
            catch (ClipToolsUnavailableException e)
            {
                logger.LogError(e, "A meal's clip could not be read");
                return Problem(StatusCodes.Status500InternalServerError,
                    "Clips cannot be read on this server right now. Take a photo instead, or describe the meal.");
            }
        }

        // The photos cross to the agent as references to this store, never as bytes on the turn, so
        // the conversation's history stays the size of its words (ADR-0020).
        var review = ReviewOf(description, photos.Count == 0 && clip is not null);
        var opened = estimates.Open(review);
        var attachments = (await PhotoUpload.StoreAsync(opened, photos, uploads, ct))
            .Concat(clip is null ? [] : await ClipUpload.StoreAsync(opened, clip, uploads, ct))
            .ToList();
        var turn = MealTurn.Text(new MealInput(
            photos.Count, description, field("clarifications"), field("language"), field("counted"),
            Filmed: clip is not null));
        return Answer(
            await channel.AskAsync(opened, turn, MealEstimateSchema.Format, attachments, patch: null, review, ct),
            opened, Log(loggers));
    }

    // Her answers continue the conversation the first estimate opened, whose first message the agent
    // still holds with her photos and words; hydration puts the photos back on the way to the model,
    // so they are neither stored nor sent again.
    private static async Task<IResult> FollowUpAsync(
        string conversation, IReadOnlyList<IFormFile> photos, IReadOnlyList<IFormFile> clips,
        Func<string, string?> field, SugaryChannel channel, OpenEstimates estimates, ILogger log,
        CancellationToken ct)
    {
        // Refused before the conversation is asked for, since continuing it starts its window again.
        if (photos.Count > 0 || clips.Count > 0)
        {
            return Problem(StatusCodes.Status400BadRequest, photos.Count > 0
                ? "Answers go without photos: the estimate already has this meal's photos."
                : "Answers go without a clip: the estimate already has this meal's clip.");
        }

        if (estimates.TryContinue(conversation) is not { } review)
        {
            return Problem(StatusCodes.Status410Gone,
                "This estimate is no longer open, so the meal has to be estimated again.");
        }

        var turn = MealTurn.FollowUp(new MealFollowUp(field("clarifications"), field("counted"), field("language")));
        return Answer(
            await channel.AskAsync(conversation, turn, MealEstimateSchema.Format, [], patch: null, review, ct),
            conversation, log);
    }

    // Every way of not getting an estimate is said, and none of them is shown as one. A 503 would
    // read in the app as "the model is not set up", so a missing agent is a bad gateway. The agent's
    // own error is logged by the channel and answered with no detail, which the app says in her
    // language as something that went wrong.
    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger("McpServerSugary.App.Estimates");

    private static IResult Answer(TurnReply reply, string conversation, ILogger log) => reply switch
    {
        TurnReply.Answered answered => Read(answered.Text, conversation, log),
        TurnReply.Failed => Problem(StatusCodes.Status502BadGateway, ""),
        TurnReply.Unanswered => Problem(StatusCodes.Status502BadGateway, "The model returned no answer."),
        TurnReply.NotListening => Problem(StatusCodes.Status502BadGateway,
            "The estimating agent is not connected right now. Try again in a moment, or count this meal by hand."),
        TurnReply.TimedOut => Problem(StatusCodes.Status504GatewayTimeout,
            "The estimate took too long. Try again, or count this meal by hand."),
        _ => throw new InvalidOperationException($"No answer for a turn that ended as {reply}.")
    };

    private const int ReviewTitleLength = 48;

    // Every estimate is read back in WebChat, named after her words: the sidebar shows the meal, not
    // the prompt the turn's text is. Cut at a word, so the row stays one line.
    private static ReviewCopy ReviewOf(string? description, bool filmedOnly)
    {
        var words = string.Join(' ', (description ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (words.Length == 0)
        {
            return new ReviewCopy { Title = filmedOnly ? "Meal video" : "Meal photo" };
        }

        if (words.Length > ReviewTitleLength)
        {
            var cut = words[..ReviewTitleLength];
            var space = cut.LastIndexOf(' ');
            words = $"{(space > 0 ? cut[..space] : cut)}\u2026";
        }

        return new ReviewCopy { Title = $"Meal: {words}" };
    }

    // Why the answer could not be read is for the log; the app says it in her language, so the
    // problem carries no English sentence for it to show.
    private static IResult Read(string reply, string conversation, ILogger log)
    {
        try
        {
            return Results.Ok(EstimateResponse.Of(MealEstimateReply.Read(reply), conversation));
        }
        catch (UnreadableEstimateException e)
        {
            log.LogWarning(e, "The estimate for {Conversation} could not be read", conversation);
            return Problem(StatusCodes.Status502BadGateway, "");
        }
    }

    private static IResult Problem(int status, string detail) => Results.Problem(detail, statusCode: status);
}

// The estimate as the standalone app answered it, and the conversation her answers continue.
public record EstimateResponse(
    IReadOnlyList<EstimatedItem> Items,
    Interval Total,
    IReadOnlyList<ClarifyingQuestion> Questions,
    IReadOnlyList<string> Notes,
    string ConversationId)
{
    public static EstimateResponse Of(MealEstimate estimate, string conversation) =>
        new(estimate.Items, estimate.Total, estimate.Questions, estimate.Notes, conversation);
}