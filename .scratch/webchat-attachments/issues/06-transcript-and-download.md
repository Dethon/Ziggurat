# 06 — Transcript and download

**What to build:** The conversation records what was sent. An attachment appears in the
transcript as a thumbnail when it is an image and as a named chip when it is not — in the
browser that sent it, in any other browser watching the same topic, and after a reload. Clicking
an attachment downloads it: the client asks the live connection for a short-lived download URL
at that moment, and the request is checked against the requester's space, because one upload
store serves every space and a long-lived URL would be readable by anyone holding it.

A message that carried attachments and no text must survive the history read, which today keeps
only text and discards messages whose text is empty.

Carries the feature's one end-to-end test: attach an image, send it, see it in the transcript,
reload, see it still there.

**Blocked by:** 04 — Composer attachments.

**Status:** resolved

- [x] Attachments appear in the transcript of the browser that sent them.
- [x] They appear in another browser watching the same topic.
- [x] They are still there after a reload.
- [x] Images render as thumbnails; other files as a chip carrying the filename.
- [x] Clicking mints a short-lived download URL and returns the file.
- [x] A download request from outside the attachment's space is refused.
- [x] A message with attachments and no text survives the history read.
- [x] An end-to-end browser test covers attach, send, see, reload, see.
