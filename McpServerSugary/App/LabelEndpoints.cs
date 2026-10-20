using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.Tools.Sugary.Estimation;
using Domain.Tools.Sugary.Labels;
using McpServerSugary.Channel;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.App;

// A nutrition label, read by a turn of the sugary agent switched to the label model for that turn
// alone. The request is the standalone app's form (one `photo`, the `language`) and the answer its
// packaged food, or a 422 with the reason the label could not be read, which the app shows as is.
public static class LabelEndpoints
{
    public static void MapLabelEndpoints(this IEndpointRouteBuilder api)
    {
        // The session cookie is SameSite=Strict, so a cross-site form post never carries it.
        api.MapPost("/labels", ReadLabelAsync).DisableAntiforgery();
    }

    // Read rather than bound, as the estimate's form is, so the cookie gate answers first.
    private static async Task<IResult> ReadLabelAsync(
        HttpRequest request, SugaryChannel channel, UploadStore uploads, IAgentCatalog catalog,
        SugarySettings settings, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return Problem(StatusCodes.Status400BadRequest, "Send the label as a form.");
        }

        var form = await request.ReadFormAsync(ct);
        if (form.Files.GetFile("photo") is not { } photo)
        {
            return Problem(StatusCodes.Status400BadRequest, "Attach a photo of the label.");
        }

        var agent = catalog.Get(settings.Agent);
        if (NotSwitchable(agent, settings.LabelModel))
        {
            return Problem(StatusCodes.Status502BadGateway,
                $"{settings.LabelModel} is not a model the sugary agent may switch to, so this label cannot be " +
                "read. Add the food by hand.");
        }

        IReadOnlyList<IFormFile> photos = [photo];
        if ((PhotoUpload.Refusal(photos, settings.Uploads)
             ?? PhotoUpload.LabelModelRefusal(photos, agent, settings.LabelModel)) is { } refusal)
        {
            return Problem(StatusCodes.Status400BadRequest, refusal);
        }

        // A conversation of its own, never one her answers can continue: a label is read once. Spelled
        // as WebChat spells one, so its review copy can be shown as a topic over it (ADR 0046).
        var conversation = ConversationIdGenerator.CreateFor(ConversationIdGenerator.NewTopicId()).ConversationId;
        var attachments = await PhotoUpload.StoreAsync(conversation, photos, uploads, ct);
        var reply = await channel.AskAsync(
            conversation, LabelTurn.Text(form["language"].ToString()), NutritionLabelSchema.Format, attachments,
            new AgentConfigPatch { Model = settings.LabelModel },
            new ReviewCopy { Title = ReviewTitle }, ct);
        return Answer(reply, loggers.CreateLogger("McpServerSugary.App.Labels"));
    }

    // Read back in WebChat as a meal is, so a misread can be traced to what the model was shown.
    private const string ReviewTitle = "Nutrition label";

    // The agent runs a patched model only if it is on its patchable list, and otherwise runs its own
    // with nothing but a log line to say so: a label read on the cheaper model would reach her as a
    // printed value. Its catalogue carries that list, so a label model off it is refused here. Where
    // the catalogue is silent — nothing registered yet — the label goes, as photos do.
    private static bool NotSwitchable(AgentCatalogEntry? agent, string labelModel) =>
        agent is { PatchableModels: { } patchable }
        && !string.Equals(agent.DefaultModel, labelModel, StringComparison.OrdinalIgnoreCase)
        && !patchable.Any(m => string.Equals(m.Id, labelModel, StringComparison.OrdinalIgnoreCase));

    // The agent's own error is logged by the channel and answered with no detail, as an estimate's is.
    private static IResult Answer(TurnReply reply, ILogger log) => reply switch
    {
        TurnReply.Answered answered => Read(answered.Text, log),
        TurnReply.Failed => Problem(StatusCodes.Status502BadGateway, ""),
        TurnReply.Unanswered => Problem(StatusCodes.Status502BadGateway, "The model returned no answer."),
        TurnReply.NotListening => Problem(StatusCodes.Status502BadGateway,
            "The label reader is not connected right now. Try again in a moment, or add the food by hand."),
        TurnReply.TimedOut => Problem(StatusCodes.Status504GatewayTimeout,
            "Reading the label took too long. Try again, or add the food by hand."),
        _ => throw new InvalidOperationException($"No answer for a turn that ended as {reply}.")
    };

    // A label that could not be read is an answer, said as one: 422 with the model's reason, or with an
    // empty detail when it gave none, which the app answers with its own "I couldn't read that label"
    // in her language (an English sentence from here would reach her Spanish screen as it is; an
    // absent detail would show the problem's title instead). An answer outside the schema is a
    // failure, like an estimate's.
    private static IResult Read(string reply, ILogger log)
    {
        try
        {
            return NutritionLabelReply.Read(reply) switch
            {
                LabelReading.Read { Label: var label } => Results.Ok(new PackagedFood(
                    label.Name, label.CarbsPer100g, label.ServingGrams, label.Unit, label.Note)),
                LabelReading.Unreadable unreadable =>
                    Problem(StatusCodes.Status422UnprocessableEntity, unreadable.Reason ?? ""),
                var other => throw new InvalidOperationException($"No answer for a label read as {other}.")
            };
        }
        catch (UnreadableEstimateException e)
        {
            // Logged, and not sent: the app words it in her language.
            log.LogWarning(e, "A label's answer could not be read");
            return Problem(StatusCodes.Status502BadGateway, "");
        }
    }

    private static IResult Problem(int status, string detail) => Results.Problem(detail, statusCode: status);
}