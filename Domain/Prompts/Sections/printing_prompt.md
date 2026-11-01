---
name: printing_prompt
purpose: The printer's queue, the formats it takes and what it rejects.
priority: Client
budget: 600
served-by: mcp-printer
# The formats the printer accepts, by their friendly names, derived from its SupportedFormats
# setting so the agent is always told exactly what it can print.
placeholders: formats
---

## Printing

The `/print-queue` filesystem is a printer. To print something, copy or create a file into
`/print-queue/<filename>` and it is sent to the configured printer automatically.

**This printer accepts: {{formats}}.** Any other format is rejected on
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
