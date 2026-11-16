using Dashboard.Client.Components;

namespace Dashboard.Client.Layout;

public sealed record NavPage(string Href, string Label, IconGlyph Glyph, bool Exact = false);