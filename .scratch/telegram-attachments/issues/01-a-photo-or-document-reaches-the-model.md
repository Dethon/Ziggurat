# 01 — A photo or a document reaches the model

**What to build:** A person sends a photo or a PDF to a Telegram bot and the model sees it. The
message qualifies under exactly the addressing rule text uses today — a caption beginning with the
command prefix, or any message in a forum thread — with the caption standing in for the text. A
message carrying files and no caption at all is a valid turn whose content is empty.

Each piece of media becomes an attachment reference. Its kind is decided by the mime type Telegram
gives, falling back to the filename extension when that is missing or generic, so a PDF sent by a
client that describes it vaguely still works and an image sent as a file is still an image. Its id
names the agent whose bot can fetch it alongside Telegram's own handle for the file. A document
keeps the filename Telegram carried; media with no filename is named after the message it arrived
in, with the extension derived from the mime type.

The channel gains the fetch-attachment tool, hidden from the model like every other
channel-protocol tool. It resolves the bot from the reference's first segment and fetches the bytes
from Telegram at the moment the agent asks, so hydration returns the file rather than a placeholder.
Nothing is stored, copied or swept on this channel (ADR 0022). An empty answer means the bytes could
not be had, which hydration turns into a placeholder naming the file.

Where the agent has a sandbox, the file lands there as a real file with no further work — that path
already exists and starts working once references flow.

Anything that is not a photo or a document is ignored exactly as it is today; refusals arrive in 02.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] A photo with a qualifying caption produces a channel message notification carrying one attachment reference and the caption as its content.
- [x] A document with a qualifying caption does the same, keeping the filename Telegram carried.
- [x] A message with attachments and no caption produces a turn with empty content rather than being dropped.
- [x] A media message that does not qualify under the addressing rule is still ignored.
- [x] Kind resolution uses the mime type first and the filename extension when it is absent or generic; an image sent as a file resolves to an image.
- [x] A reference id names the agent and Telegram's file handle; media with no filename is named after its message id with an extension derived from the mime type.
- [x] The fetch-attachment tool returns the bytes for a known reference, resolving the bot from the reference alone with no prior chat mapping.
- [x] The fetch-attachment tool answers empty for a reference it cannot fetch, rather than failing.
- [x] Domain comments stating that only the SignalR channel populates attachments are corrected.
- [x] Tests drive real Telegram updates through the polling service against a mocked bot client and assert on the notification reaching the channel inbox; the tool is tested through its own entry point. The existing test asserting that a photo message is ignored inverts.
