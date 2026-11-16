namespace Dashboard.Client.Components;

// The charts' colours, written once. ApexCharts draws them into SVG attributes, where the stylesheet's
// custom properties do not reach, so they are spelled here to match app.css's tokens.
public static class ChartTheme
{
    public const string Text = "#94a3b8";
    public const string Grid = "rgba(148, 163, 184, 0.12)";
    public const string Label = "#e2e8f0";
    public const string Font = "Inter, ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif";

    public static readonly List<string> Palette =
        ["#60a5fa", "#a78bfa", "#34d399", "#fbbf24", "#f472b6", "#22d3ee", "#fb923c", "#f87171"];
}