using Microsoft.AspNetCore.Components;

namespace Dashboard.Client.Components;

// Which way a sortable column is pointing, drawn once. Seven pages carried their own copy of the
// same pair of glyphs, so the arrow was seven decisions that happened to agree; it is now one.
public static class SortArrow
{
    // Nothing for a column that is not the sorted one: the header shows its arrow only where the
    // arrow means something, which is what the empty string did before.
    public static MarkupString For(string column, string sortColumn, bool ascending) =>
        column != sortColumn ? default : ascending ? _up : _down;

    private static readonly MarkupString _up = Triangle("M12 9l5 6H7Z");
    private static readonly MarkupString _down = Triangle("M12 15l-5-6h10Z");

    private static MarkupString Triangle(string path) =>
        new($"""<svg class="sort-arrow" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="{path}" /></svg>""");
}