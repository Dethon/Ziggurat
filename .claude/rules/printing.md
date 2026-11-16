---
paths:
  - "McpServerPrinter/**"
  - "Domain/Tools/Printing/**"
  - "Infrastructure/Printing/**"
  - "Infrastructure/Clients/Printer/**"
  - "Domain/Prompts/PrintingPrompt.cs"
  - "Domain/Prompts/Sections/printing_prompt.md"
  - "Domain/Contracts/IPrinter*.cs"
  - "Domain/Contracts/IPrintSpool.cs"
  - "Domain/DTOs/Printing/**"
---

# Printing Architecture

`McpServerPrinter` is a non-disk MCP filesystem server exposing **`filesystem://print-queue`** (mount `/print-queue`) backed by `PrinterQueueFileSystem` (`Domain/Tools/Printing/Vfs/`). Copying or creating a file into `/print-queue/<filename>` (bytes via `fs_blob_write` chunk streaming) immediately submits it to the single configured printer; `fs_delete` on an active job cancels it; `move` and `exec` are not implemented, so the mount does not advertise them. Two contracts back it:
- **`IPrinterClient`** — `IppPrinterClient` (`Infrastructure/Clients/Printer/`), a `SharpIppNext` + `HttpClient` adapter against `PRINTERURI`, mapping `Print-Job`/`Get-Jobs`/`Cancel-Job`. `IppJobStateMapper` maps IPP states to `PrintJobState`. Get-Jobs requests `job-state` so active jobs aren't pruned mid-print, and `GetActiveJobsAsync` defensively drops non-active states for printers that ignore `WhichJobs.NotCompleted`.
- **`IPrintSpool`** — `PrintSpool` (`Infrastructure/Printing/`), disk-backed under `/spool`, keyed by filename, holding `{JobId, ContentType, Bytes, SubmittedAt, MissingSince}` so `read`/`search`/`edit`/blob read-back work while a job is active (blobs use the `.blob` suffix). `PrintQueueCoordinator` prunes during reconciliation but **debounces absence**: a job is dropped only after staying absent from the printer's active set past `ReconcileGraceMilliseconds`, so a just-submitted job (or a transient empty `Get-Jobs`) isn't lost mid-print.

Accepted formats are configurable via **`SupportedFormats`** (default `text,jpeg,pwg-raster,urf,pcl`); anything else is rejected on copy-in. It is the single source of truth — the `printing_prompt` (`PrintingPrompt.Build`/`DescribeFormats`) and the resource description derive their advertised list from it, so accepted and advertised can't drift. Submission is `application/octet-stream` (IPP printers reject unknown content types); text is CRLF-normalized (content-sniffed for octet-stream copies) to stop staircase printing, images use `print-scaling=fit`, and `PrintableContent` (`Domain/Tools/Printing/`) does detection/normalization. `/print-queue/status.json` is a read-only view; finished jobs disappear from the listing.
