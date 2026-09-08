namespace Domain.Prompts;

public static class PrintingPrompt
{
    public const string Name = "printing_prompt";

    public const string Description =
        "Explains how to print via the /print-queue filesystem: which formats the printer accepts, converting anything else first, copy to print, remove to cancel.";

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
        $"""
        ## Printing

        The `/print-queue` filesystem is a printer. To print something, copy or create a file into
        `/print-queue/<filename>` and it is sent to the configured printer automatically.

        **This printer accepts: {DescribeFormats(supportedFormats)}.** Any other format is rejected on
        copy-in (the printer cannot render it and it would otherwise print as garbage).

        - **Convert before printing.** If what you want to print is not an accepted format, first
          transform it into one — typically plain text or a JPEG. For example: render a PDF, web page,
          chart, or PNG to a JPEG; or extract a document's text and `text_create` it as a `.txt`. Use your
          available tools (e.g. the sandbox) to do the conversion.
        - Examples: `domain__filesystem__text_create` on `/print-queue/note.txt` with text content, or copy `/vault/photo.jpg`
          to `/print-queue/photo.jpg`.
        - To **cancel** a job that has not finished printing yet, use the `remove` tool. If it has
          already finished, it is gone from the queue and removal is a no-op.
        - Read `/print-queue/status.json` to see every queued job and its state
          (queued / pending / processing).
        - Finished jobs disappear from the listing automatically.
        - `move` and `exec` are not supported on this filesystem. To reprint an edited text document
          use `text_edit` (text only); it cancels the old job and queues the new version.
        """;
}