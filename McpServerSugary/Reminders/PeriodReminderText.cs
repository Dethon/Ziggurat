using System.Globalization;

namespace McpServerSugary.Reminders;

// Her period reminder as her phone shows it, in the app's language; a tap opens the Cycle tab.
public static class PeriodReminderText
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public static PenNotification For(DateOnly likeliest, int daysTo, string language)
    {
        var spanish = language == "es";
        var day = likeliest.ToString(spanish ? "d 'de' MMMM" : "d MMMM", spanish ? Spanish : English);
        var (title, body) = spanish
            ? ($"Tu regla podría empezar en unos {daysTo} {(daysTo == 1 ? "día" : "días")}", $"Hacia el {day}, según tus últimos ciclos.")
            : ($"Your period may start in about {daysTo} {(daysTo == 1 ? "day" : "days")}", $"Around {day}, going by your recent cycles.");
        return new PenNotification(title, body, "period", "/#cycle");
    }
}