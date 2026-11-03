using Domain.Tools.Sugary.Estimation;

namespace WebChat.Client.Components.Chat;

// The structured answers the chat knows by name: which app asks for each. A format not named here is
// still drawn, as an outline of its JSON.
public static class StructuredAnswers
{
    public static string? AppOf(string format) => format switch
    {
        MealEstimateSchema.Name => "Sugary app",
        _ => null
    };
}