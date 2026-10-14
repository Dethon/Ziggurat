using Domain.Tools.Sugary.Estimation;
using McpServerSugary.Channel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace McpServerSugary.App;

// The app's estimate, answered by a turn of the sugary agent. The request is the standalone app's
// form and the answer its estimate, unchanged, so the app did not have to change with it. Each
// estimate is a conversation of its own for now; follow-up answers on the same conversation are a
// later ticket (06), as are photos (05).
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
    private static async Task<IResult> EstimateAsync(HttpRequest request, SugaryChannel channel, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return Problem(StatusCodes.Status400BadRequest, "Send the meal as a form.");
        }

        var form = await request.ReadFormAsync(ct);
        string? Field(string name) => form.TryGetValue(name, out var value) ? value.ToString() : null;
        var description = Field("description");

        // Refused rather than dropped: an estimate from her words alone, when she meant the photo
        // to show the meal, would be a worse number presented as the one she asked for.
        if (form.Files.GetFiles("photos").Count > 0)
        {
            return Problem(StatusCodes.Status400BadRequest,
                "Photos cannot be estimated yet. Describe the meal in words instead.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return Problem(StatusCodes.Status400BadRequest, "Add a photo or describe the meal.");
        }

        var turn = MealTurn.Text(new MealInput(
            0, description, Field("clarifications"), Field("language"), Field("counted")));
        var reply = await channel.AskAsync(Guid.NewGuid().ToString("N"), turn, MealEstimateSchema.Format, ct);

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