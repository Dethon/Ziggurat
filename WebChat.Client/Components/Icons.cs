namespace WebChat.Client.Components;

// What an icon is made of, apart from the wrapper <Icon> puts round it. A filled glyph carries no
// stroke width; a stroked one carries the width it was drawn at, and 1.5 and 2 are not
// interchangeable — the empty state's bubble is a lighter line than the toast's cross on purpose.
public sealed record IconGlyph(string Body, string ViewBox = "0 0 24 24", double? StrokeWidth = null);

// Every icon this app draws, in one place. A cross appearing in four controls is one definition
// rather than four that happen to agree, and the whole set can be read at once. Nothing here says
// how big an icon is or what colour it takes — that belongs to the control it sits in.
public static class Icons
{
    // Filled, on a 24-unit grid. The composer's three keep the viewBoxes they were drawn at:
    // rescaling their path data by hand to match the others would be a transcription error waiting
    // to happen, and the viewBox costs nothing.

    public static readonly IconGlyph Close = Filled(
        "M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12Z");

    public static readonly IconGlyph Check = Filled("M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41Z");

    public static readonly IconGlyph Archive = Filled(
        "M20.54 5.23 19.15 3.55C18.88 3.21 18.47 3 18 3H6c-.47 0-.88.21-1.15.55L3.46 5.23C3.17 5.57 3 6.02 "
        + "3 6.5V19c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V6.5c0-.48-.17-.93-.46-1.27ZM12 17.5 6.5 12H10v-2h4v2h3.5ZM5.12 "
        + "5l.81-1h12l.94 1Z");

    public static readonly IconGlyph Trash = Filled(
        "M7 21q-.825 0-1.412-.587Q5 19.825 5 19V6H4V4h5V3h6v1h5v2h-1v13q0 .825-.587 1.413Q17.825 21 17 "
        + "21Zm10-15H7v13h10ZM9 17h2V8H9Zm4 0h2V8h-2ZM7 6v13Z");

    public static readonly IconGlyph Gear = Filled(
        "M19.14 12.94c.04-.3.06-.61.06-.94s-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54A.48.48 "
        + "0 0 0 13.9 2h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 "
        + "0-.59.22L2.71 8.47c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 "
        + "1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 "
        + "2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 "
        + "0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61ZM12 15.6A3.6 3.6 0 1 1 12 8.4a3.6 3.6 0 0 1 0 7.2Z");

    // A lemon on its side with a nub at each end, marking a model that runs on the Lemonade chat
    // host. Filled like the gear beside it, so the two read as one family.
    public static readonly IconGlyph Lemon = new(
        """
        <ellipse cx="12" cy="12" rx="8" ry="5.4" transform="rotate(-35 12 12)" />
        <circle cx="4.9" cy="16.9" r="1.7" />
        <circle cx="19.1" cy="7.1" r="1.7" />
        """);

    // Points right when shut and is turned by CSS when open, so a toggle is one drawing rather than
    // two that have to agree about weight.
    public static readonly IconGlyph Triangle = Filled("M9 6l6 6-6 6Z");

    public static readonly IconGlyph CaretDown = Filled("M7 10h10l-5 5Z");

    public static readonly IconGlyph ChevronUp = Filled("M12 10.8 7.4 15.4 6 14l6-6 6 6-1.4 1.4Z");

    public static readonly IconGlyph Lock = Filled(
        "M6 22q-.825 0-1.412-.587Q4 20.825 4 20V10q0-.825.588-1.412Q5.175 8 6 8h1V6q0-2.075 1.463-3.538Q9.925 "
        + "1 12 1t3.538 1.462Q17 3.925 17 6v2h1q.825 0 1.413.588Q20 9.175 20 10v10q0 .825-.587 1.413Q18.825 22 18 "
        + "22Zm6-5q.825 0 1.413-.587Q14 15.825 14 15t-.587-1.413Q12.825 13 12 13t-1.412.587Q10 14.175 10 15t.588 "
        + "1.413Q11.175 17 12 17ZM9 8h6V6q0-1.25-.875-2.125T12 3q-1.25 0-2.125.875T9 6Z");

    public static readonly IconGlyph Microphone = Filled(
        "M12 14q-1.25 0-2.125-.875T9 11V5q0-1.25.875-2.125T12 2q1.25 0 2.125.875T15 5v6q0 1.25-.875 2.125T12 "
        + "14Zm-1 7v-3.075q-2.6-.35-4.3-2.325T5 11h2q0 2.075 1.463 3.537T12 16q2.075 0 3.538-1.463T17 11h2q0 "
        + "2.625-1.7 4.6T13 17.925V21h-2Z");

    public static readonly IconGlyph Sync = Filled(
        "M12 4V1L8 5l4 4V6a6 6 0 0 1 5.3 8.8l1.46 1.46A8 8 0 0 0 12 4Zm0 14a6 6 0 0 1-5.3-8.8L5.24 7.74A8 8 0 0 0 "
        + "12 20v3l4-4-4-4Z");

    public static readonly IconGlyph Ring = Filled(
        "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm0 18a8 8 0 1 1 0-16 8 8 0 0 1 0 16Z");

    public static readonly IconGlyph PaperPlane = Filled(
        "M476 3.2 12.5 270.6c-18.1 10.4-15.8 35.6 2.2 43.2L121 358.4l287.3-253.2c5.5-4.9 13.3 2.6 8.6 8.3L176 "
        + "407v80.5c0 23.6 28.5 32.9 42.5 15.8L282 426l124.6 52.2c14.2 6 30.4-2.9 33-18.2l72-432C515 7.8 493.3-6.8 "
        + "476 3.2Z",
        "-81 -81 674 674");

    public static readonly IconGlyph Paperclip = Filled(
        "M364.2 83.8c-24.4-24.4-64-24.4-88.4 0l-184 184c-42.1 42.1-42.1 110.3 0 152.4s110.3 42.1 152.4 0l152-152c10.9-10.9 "
        + "28.7-10.9 39.6 0s10.9 28.7 0 39.6l-152 152c-64 64-167.6 64-231.6 0s-64-167.6 0-231.6l184-184c46.3-46.3 "
        + "121.3-46.3 167.6 0s46.3 121.3 0 167.6l-176 176c-28.6 28.6-75 28.6-103.6 0s-28.6-75 0-103.6l144-144c10.9-10.9 "
        + "28.7-10.9 39.6 0s10.9 28.7 0 39.6l-144 144c-6.7 6.7-6.7 17.7 0 24.4s17.7 6.7 24.4 0l176-176c24.4-24.4 "
        + "24.4-64 0-88.4z",
        "-104 -69 656 656");

    // A filled square reads heavier than an outline of the same width, so this one is drawn a little
    // under the size of the plane it stands in for. The viewBox is the inset.
    public static readonly IconGlyph Square = Filled("M8 6h8q2 0 2 2v8q0 2-2 2H8q-2 0-2-2V8q0-2 2-2Z", "2.9 2.9 18.2 18.2");

    // Stroked, on the same 24-unit grid. These came from a different set than the filled ones and
    // are kept apart on purpose: mixing a filled and a stroked icon inside one control looks like a
    // rendering bug rather than a choice.

    public static readonly IconGlyph CloseStroked = Stroked(
        """<line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />""");

    public static readonly IconGlyph CheckStroked = Stroked("""<polyline points="20 6 9 17 4 12" />""");

    public static readonly IconGlyph Copy = Stroked(
        """<rect x="9" y="9" width="13" height="13" rx="2" ry="2" /><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />""");

    public static readonly IconGlyph Retry = Stroked(
        """<polyline points="23 4 23 10 17 10" /><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" />""");

    public static readonly IconGlyph Sun = Stroked(
        """
        <circle cx="12" cy="12" r="5" />
        <line x1="12" y1="1" x2="12" y2="3" /><line x1="12" y1="21" x2="12" y2="23" />
        <line x1="4.22" y1="4.22" x2="5.64" y2="5.64" /><line x1="18.36" y1="18.36" x2="19.78" y2="19.78" />
        <line x1="1" y1="12" x2="3" y2="12" /><line x1="21" y1="12" x2="23" y2="12" />
        <line x1="4.22" y1="19.78" x2="5.64" y2="18.36" /><line x1="18.36" y1="5.64" x2="19.78" y2="4.22" />
        """);

    public static readonly IconGlyph Moon = Stroked("""<path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />""");

    // Drawn a line lighter than the rest, because it is an illustration filling the empty chat
    // rather than a control someone has to find.
    public static readonly IconGlyph ChatBubble = Stroked(
        """
        <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
        <circle cx="9" cy="10" r="1" fill="currentColor" />
        <circle cx="12" cy="10" r="1" fill="currentColor" />
        <circle cx="15" cy="10" r="1" fill="currentColor" />
        """,
        1.5);

    private static IconGlyph Filled(string body, string viewBox = "0 0 24 24") =>
        new($"""<path d="{body}" />""", viewBox);

    private static IconGlyph Stroked(string body, double width = 2) =>
        new(body, StrokeWidth: width);
}