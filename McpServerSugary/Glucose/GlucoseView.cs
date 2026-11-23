namespace McpServerSugary.Glucose;

// Which values the Glucose tab reads: smoothed (GlucoseSmoothing) unless she asks for the sensor's own.
public enum GlucoseView
{
    Smoothed,
    Raw
}

public static class GlucoseViews
{
    // Null for a word that names no view; no word at all is the default.
    public static GlucoseView? Of(string? word) => word switch
    {
        null or "smoothed" => GlucoseView.Smoothed,
        "raw" => GlucoseView.Raw,
        _ => null
    };
}