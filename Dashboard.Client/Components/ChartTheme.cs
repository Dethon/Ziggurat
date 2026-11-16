namespace Dashboard.Client.Components;

// The charts' colours, written once. ApexCharts draws them into SVG attributes, where the stylesheet's
// custom properties do not reach, so they are spelled here to match app.css's tokens.
public static class ChartTheme
{
    public const string Text = "#94a3b8";
    public const string Grid = "rgba(148, 163, 184, 0.12)";
    public const string Label = "#e2e8f0";
    public const string Font = "ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif";

    public const string Surface = "#111723";

    // A categorical order validated for this dark surface (lightness band, colour-blind separation of
    // neighbours, contrast): series take these in order, so the eighth is the last before "other".
    public static readonly List<string> Palette =
        ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#0ca30c", "#9085e9", "#e66767"];
}