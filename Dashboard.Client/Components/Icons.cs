namespace Dashboard.Client.Components;

// What an icon is made of, apart from the wrapper <Icon> puts round it. Everything here is a
// filled 24-unit glyph; if a stroked one ever arrives, this record grows a stroke width the way
// WebChat's already has.
public sealed record IconGlyph(string Body, string ViewBox = "0 0 24 24");

// Every icon this app draws, in one place. The sidebar is the whole of it plus two arrows and a
// tick, and having them together is what stops the next page inventing an eighth style. Nothing
// here says how big an icon is or what colour it takes — that belongs to the control it sits in.
public static class Icons
{
    public static readonly IconGlyph Dashboard = Filled(
        "M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z");

    public static readonly IconGlyph Currency = Filled(
        "M11.8 10.9c-2.27-.59-3-1.2-3-2.15 0-1.09 1.01-1.85 2.7-1.85 1.78 0 2.44.85 2.5 2.1h2.21c-.07-1.72-1.12-3.3-3.21-3.81V3h-3v2.16c-1.94.42-3.5 "
        + "1.68-3.5 3.61 0 2.31 1.91 3.46 4.7 4.13 2.5.6 3 1.48 3 2.41 0 .69-.49 1.79-2.7 1.79-2.06 0-2.87-.92-2.98-2.1h-2.2c.12 "
        + "2.19 1.76 3.42 3.68 3.83V21h3v-2.15c1.95-.37 3.5-1.5 3.5-3.55 0-2.84-2.43-3.81-4.7-4.4z");

    public static readonly IconGlyph Gear = Filled(
        "M19.14 12.94c.04-.3.06-.61.06-.94s-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54A.48.48 "
        + "0 0 0 13.9 2h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 "
        + "0-.59.22L2.71 8.47c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 "
        + "1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 "
        + "2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 "
        + "0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61ZM12 15.6A3.6 3.6 0 1 1 12 8.4a3.6 3.6 0 0 1 0 7.2Z");

    public static readonly IconGlyph Chip = Filled(
        "M15 9H9v6h6V9zm-2 4h-2v-2h2v2zm8-2V9h-2V7c0-1.1-.9-2-2-2h-2V3h-2v2h-2V3H9v2H7c-1.1 0-2 .9-2 2v2H3v2h2v2H3v2h2v2c0 "
        + "1.1.9 2 2 2h2v2h2v-2h2v2h2v-2h2c1.1 0 2-.9 2-2v-2h2v-2h-2v-2h2zm-4 6H7V7h10v10z");

    public static readonly IconGlyph Warning = Filled("M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z");

    public static readonly IconGlyph Calendar = Filled(
        "M17 12h-5v5h5v-5zM16 1v2H8V1H6v2H5c-1.11 0-1.99.9-1.99 2L3 19a2 2 0 0 0 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2h-1V1h-2zm3 18H5V8h14v11z");

    // A stopwatch rather than another clock face: schedules are when work fires, latency is how
    // long it took, and two clocks in the same sidebar would read as the same page twice.
    public static readonly IconGlyph Stopwatch = Filled(
        "M15 1H9v2h6V1zm-4 13h2V8h-2v6zm8.03-6.61 1.42-1.42c-.43-.51-.9-.99-1.41-1.41l-1.42 1.42A8.962 8.962 0 0 0 12 4a9 9 0 0 0-9 "
        + "9c0 4.97 4.02 9 9 9a8.994 8.994 0 0 0 7.03-14.61zM12 20c-3.87 0-7-3.13-7-7s3.13-7 7-7 7 3.13 7 7-3.13 7-7 7z");

    public static readonly IconGlyph Speaker = Filled(
        "M3 9v6h4l5 5V4L7 9H3z",
        // Two shapes, one icon: the cone and the waves coming off it.
        "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 "
        + "6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");

    public static readonly IconGlyph TriangleUp = Filled("M12 9l5 6H7Z");

    public static readonly IconGlyph TriangleDown = Filled("M12 15l-5-6h10Z");

    public static readonly IconGlyph Check = Filled("M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41Z");

    private static IconGlyph Filled(params string[] paths) =>
        new(string.Concat(paths.Select(p => $"""<path d="{p}" />""")));
}