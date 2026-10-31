namespace Domain.Prompts;

// The printer's queue and the formats it takes (Sections/printing_prompt.md), served by mcp-printer.
public static class PrintingPrompt
{
    public const string Name = "printing_prompt";

    public const string Description =
        "Explains how to print via the /print-queue filesystem: which formats the printer accepts, converting anything else first, copy to print, remove to cancel.";

    private static readonly PromptTemplate _template = PromptDocuments.Section(Name).Template("formats");

    // Friendly names for the format tokens used by PrintableContent / PrinterSettings.SupportedFormats.
    private static readonly Dictionary<string, string> _formatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = "plain text",
        ["jpeg"] = "JPEG images",
        ["png"] = "PNG images",
        ["gif"] = "GIF images",
        ["bmp"] = "BMP images",
        ["tiff"] = "TIFF images",
        ["pdf"] = "PDF documents",
        ["pwg-raster"] = "PWG Raster",
        ["urf"] = "Apple URF",
        ["pcl"] = "PCL"
    };

    // Human-readable list of accepted formats, derived from the printer's SupportedFormats setting so
    // the agent is always told exactly what it can print — no drift between config and guidance.
    public static string DescribeFormats(string supportedFormats)
    {
        var names = (supportedFormats ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => _formatNames.GetValueOrDefault(token, token))
            .ToList();

        return names.Count == 0 ? "nothing" : string.Join(", ", names);
    }

    public static string Build(string supportedFormats) =>
        _template.Fill(("formats", DescribeFormats(supportedFormats)));
}