using System.Text;
using Domain.Contracts;
using Domain.DTOs.Printing;
using SharpIpp;
using SharpIpp.Models.Requests;
using SharpIpp.Protocol.Models;

namespace Infrastructure.Clients.Printer;

// Talks IPP over HTTP to a single configured printer (a CUPS server or a direct-IPP printer).
// The document is submitted with documentFormat (default "application/octet-stream", the IPP
// auto-sense type) rather than the spooled content type, which printers often reject.
public sealed class IppPrinterClient(ISharpIppClient client, Uri printerUri, string documentFormat, string printScaling) : IPrinterClient
{
    public async Task<PrintJobHandle> SubmitAsync(string jobName, string contentType, ReadOnlyMemory<byte> document, CancellationToken ct)
    {
        var isText = IsTextPayload(contentType, document);
        await using var stream = new MemoryStream(PreparePayload(contentType, document), writable: false);
        var request = new PrintJobRequest
        {
            Document = stream,
            OperationAttributes = new PrintJobOperationAttributes
            {
                PrinterUri = printerUri,
                JobName = jobName,
                DocumentName = jobName,
                DocumentFormat = new DocumentFormat(documentFormat)
            },
            // Images otherwise default to print-scaling=auto, which this printer renders as a
            // borderless fill; request scaling (default "fit") so they size within the page margins.
            // Text is left alone so the printer's text engine renders it normally.
            JobTemplateAttributes = isText ? null : new JobTemplateAttributes { PrintScaling = ParseScaling(printScaling) }
        };

        var response = await client.PrintJobAsync(request, ct);
        return new PrintJobHandle(response.JobAttributes?.JobId ?? 0);
    }

    public async Task<IReadOnlyList<PrintJobStatus>> GetActiveJobsAsync(CancellationToken ct)
    {
        var request = new GetJobsRequest
        {
            OperationAttributes = new GetJobsOperationAttributes
            {
                PrinterUri = printerUri,
                WhichJobs = WhichJobs.NotCompleted,
                // Without an explicit request many printers (e.g. HP) return only job-id/job-uri and
                // omit job-state, which would make every active job look stateless and get pruned
                // from the queue mid-print. Ask for the attributes we actually need.
                RequestedAttributes = new[] { "job-id", "job-state", "job-name" }
            }
        };

        var response = await client.GetJobsAsync(request, ct);
        // WhichJobs.NotCompleted should restrict the result server-side, but some printers ignore it
        // and return finished jobs too — which would keep those jobs "active" forever and stop the
        // queue from ever pruning them. Defensively drop any job whose reported state isn't active.
        // A printer that omits job-state entirely is kept (mapped to Processing) since we asked for
        // not-completed jobs and have nothing better to go on. IPP's out-of-band no-value counts as
        // omitted: read through the implicit conversion it would be job 0 in state 0, and pruned.
        return (response.JobsAttributes ?? [])
            .Select(j => (Id: Known(j.JobId), j.JobName, State: Known(j.JobState)))
            .Where(j => j.Id is not null)
            .Where(j => j.State is null || IppJobStateMapper.IsActive(j.State.Value))
            .Select(j => new PrintJobStatus(
                j.Id!.Value,
                j.JobName?.Value ?? string.Empty,
                j.State is not null ? IppJobStateMapper.Map(j.State.Value) : PrintJobState.Processing))
            .ToList();
    }

    private static T? Known<T>(IppValue<T>? attribute) where T : struct =>
        attribute is { IsValue: true } known ? known.Value : null;

    public async Task CancelAsync(int jobId, CancellationToken ct)
    {
        var request = new CancelJobRequest
        {
            OperationAttributes = new CancelJobOperationAttributes
            {
                PrinterUri = printerUri,
                JobId = jobId
            }
        };

        await client.CancelJobAsync(request, ct);
    }

    // Raw text sent to a printer staircases when lines end in bare LF: the printer feeds a line
    // but does not return the carriage. Normalize text payloads to CRLF; binary payloads are passed
    // through untouched so their bytes are never rewritten.
    internal static byte[] PreparePayload(string contentType, ReadOnlyMemory<byte> document)
    {
        if (!IsTextPayload(contentType, document))
        {
            return document.ToArray();
        }

        var text = Encoding.UTF8.GetString(document.Span);
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        return Encoding.UTF8.GetBytes(normalized);
    }

    // text/* is text. The catch-all "application/octet-stream" (used by cross-backend copies, which
    // lose the real content type) is content-sniffed: text iff it has no NUL byte and is valid UTF-8.
    // Any other explicit type (image/*, application/pdf, ...) is treated as binary.
    internal static bool IsTextPayload(string contentType, ReadOnlyMemory<byte> document)
    {
        var type = contentType ?? "";
        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (type.Length > 0 && !type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var span = document.Span;
        if (span.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static PrintScaling ParseScaling(string value) => (value ?? "").ToLowerInvariant() switch
    {
        "auto" => PrintScaling.Auto,
        "auto-fit" => PrintScaling.AutoFit,
        "fill" => PrintScaling.Fill,
        "none" => PrintScaling.None,
        _ => PrintScaling.Fit
    };
}