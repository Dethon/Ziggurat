using Domain.Tools.Sugary.Estimation;
using Domain.Tools.Sugary.Labels;
using WebChat.Client.Models;

namespace WebChat.Client.Components.Chat;

// The structured answers the chat knows by name: which app asks for each. A format not named here is
// still drawn, as an outline of its JSON.
public static class StructuredAnswers
{
    public static string? AppOf(string format) => format switch
    {
        MealEstimateSchema.Name or NutritionLabelSchema.Name => "Sugary app",
        _ => null
    };

    // A Sugary estimate, read as the app reads it, so the total shown is the one the app showed; one
    // it cannot read is drawn as an outline instead.
    public static MealEstimate? MealEstimateOf(ChatMessageModel message)
    {
        if (message.Role != "assistant" || message.ResponseFormat != MealEstimateSchema.Name)
        {
            return null;
        }

        try
        {
            return MealEstimateReply.Read(message.Content);
        }
        catch (UnreadableEstimateException)
        {
            return null;
        }
    }
}