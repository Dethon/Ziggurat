using Domain.Security;
using Mcp.Hosting;

namespace McpServerPrinter.Settings;

public record PrinterSettings : IMcpHostSettings
{
    // The deployment secret this server's /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required string PrinterUri { get; init; }
    public string SpoolPath { get; init; } = "/spool";
    public int SubmitDebounceMilliseconds { get; init; } = 750;
    public int TickIntervalMilliseconds { get; init; } = 500;

    // How long a submitted job must stay absent from the printer's active set before it is pruned from
    // the queue. Debounces the gap between submitting a job and the printer registering it, and any
    // transient empty Get-Jobs response, so still-printing jobs aren't dropped early.
    public int ReconcileGraceMilliseconds { get; init; } = 5000;

    // IPP document-format sent to the printer. "application/octet-stream" is the IPP
    // auto-sense default supported by virtually all printers (the printer detects the real
    // format from the bytes); sending a specific type like "text/plain" is rejected by
    // printers that do not advertise it.
    public string DocumentFormat { get; init; } = "application/octet-stream";

    // IPP print-scaling for image/binary jobs: auto | auto-fit | fill | fit | none. "fit" sizes the
    // image within the page margins; the printer's "auto" default tends to fill the page borderless.
    public string PrintScaling { get; init; } = "fit";

    // Comma-separated format tokens accepted into the queue (matched by PrintableContent.DetectFormat).
    // This is the single source of truth for what the agent may print: the printing prompt and the
    // print-queue resource description derive their accepted-format list from it, so whatever is listed
    // here is advertised to the agent as usable. Files of any other format are rejected on copy-in.
    public string SupportedFormats { get; init; } = "text,jpeg,pwg-raster,urf,pcl";
}