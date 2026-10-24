using System.Globalization;
using Domain.Tools.Sugary;
using McpServerSugary.Store;

namespace McpServerSugary.Reminders;

// A reminder as her phone shows it, in the app's language. The tag is one per kind, so the day's
// reminder takes the place of the day before's rather than stacking under it.
public record PenNotification(string Title, string Body, string Tag, string Url);

public static class PenReminderText
{
    public static readonly IReadOnlySet<string?> Languages = new HashSet<string?> { "es", "en" };

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public static PenNotification For(Pen pen, PenReminder reminder, DateOnly today, string language)
    {
        var spanish = language == "es";
        var started = pen.OpenedOn.ToString(spanish ? "d 'de' MMMM" : "d MMMM", spanish ? Spanish : English);
        var used = today.DayNumber - pen.OpenedOn.DayNumber;
        var (title, body) = (reminder, spanish) switch
        {
            (PenReminder.Tomorrow, true) =>
                ($"Mañana toca cambiar la pluma de {pen.Insulin}", $"La empezaste el {started} y dura {pen.Days} días."),
            (PenReminder.Tomorrow, false) =>
                ($"Change your {pen.Insulin} pen tomorrow", $"You started it on {started} and it lasts {pen.Days} days."),
            (_, true) =>
                ($"Toca una pluma nueva de {pen.Insulin}", $"Hace {used} días que la empezaste, el {started}."),
            _ =>
                ($"Time for a new {pen.Insulin} pen", $"It's been {used} days since you started it on {started}.")
        };
        return new PenNotification(title, body, $"pen-{SugaryTables.Word(pen.Kind)}", "/");
    }
}