using System.Text.Json.Nodes;

namespace Domain.Tools.HomeAssistant.Vfs;

// The window a recorder read covers, resolved once for both reads: a length counted back from the
// end (now, or the end given), or an explicit start that excludes the length. Strings stay the
// caller's (HaDateTimeText).
internal static class HaWindow
{
    public static (string Start, string End) Resolve(
        JsonObject data, TimeProvider time, string lengthArgument, Func<double, TimeSpan> length, double defaultLength)
    {
        var given = Positive(data, lengthArgument);
        var start = Text(data, "start_date_time");
        var end = Text(data, "end_date_time");

        if (given is not null && start is not null)
        {
            throw new ArgumentException($"Give either --{lengthArgument} or --start_date_time, not both.");
        }

        var now = HaDateTimeText.Now(time);
        if (start is not null)
        {
            return (start, end ?? now);
        }

        var span = length(given ?? defaultLength);
        end ??= now;
        return (HaDateTimeText.Shift(end, -span, "end_date_time"), end);
    }

    public static string? Text(JsonObject data, string name) => data[name] switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => string.IsNullOrWhiteSpace(text) ? null : text,
        JsonNode node => node.ToJsonString(),
        null => null
    };

    public static double? Positive(JsonObject data, string name)
    {
        if (data[name] is not JsonValue value)
        {
            return null;
        }
        var number = value.GetValue<double>();
        return number > 0
            ? number
            : throw new ArgumentException($"--{name} expects a positive number, got {number}.");
    }

    public static int? WholeNumber(JsonObject data, string name)
    {
        var number = Positive(data, name);
        if (number is null)
        {
            return null;
        }
        return number % 1 == 0
            ? (int)number
            : throw new ArgumentException($"--{name} expects a whole number, got {number}.");
    }
}