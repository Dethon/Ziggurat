# 01 — Upload store and its endpoints

**What to build:** A person composing a message can hand a file to the system and get an
attachment reference back, and anything holding that reference can read the file again. The
browser asks the live connection for an upload ticket scoped to the topic it is composing in,
then sends each file over HTTP and receives a reference naming where the file rests, its media
type, its filename and its size. A request without a valid ticket, with an expired one, or with
one minted for a different topic gets nothing. Files that are too large or of a kind this
feature does not accept are refused with a reason a person can read.

Nothing in the chat uses this yet. It is verifiable on its own: mint a ticket, upload a file,
read it back, and watch every refusal case be refused.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The live connection mints an upload ticket scoped to one topic with a short, configured TTL.
- [ ] A file uploaded with a valid ticket is stored and answered with an attachment reference.
- [ ] The stored bytes can be read back through the download endpoint, unchanged.
- [ ] Upload is refused with no ticket, an expired ticket, or a ticket for another topic.
- [ ] A file above the configured maximum size is refused, and so is a media type outside images and PDF.
- [ ] More files than the configured per-message maximum are refused.
- [ ] One file per HTTP request; the hub's own message size limit is untouched.
- [ ] Maximum bytes, maximum files and ticket TTL are settings, not constants.
- [ ] The upload store's location is a per-deployment volume, and the reverse proxy routes the upload and download paths to the channel server.
- [ ] Endpoint tests drive the real routes over a test-server host with real multipart bodies, in the style of the voice channel's endpoint suites.
