# 02 — The tool answers honestly for an image

**What to build:** the agent stops garbling images. Asked to read a screenshot, `file_read`
recognises it as an image and answers a small envelope naming the virtual path as the caller spelled
it, the media type and the byte size — and says plainly that the image was not shown, because
nothing yet carries bytes to the model. Asked to read a zip file, it refuses by name instead of
returning mojibake. Reading a text file is untouched.

The envelope's `shown` flag always comes with a reason when it is false: the image is over the
configured ceiling, the model running this turn does not accept images, or this host has no store to
put the bytes in. `offset` and `limit` mean nothing for an image, so they are ignored rather than
rejected, and the envelope says so, because a habit carried over from reading text should not cost
the model a turn.

The size ceiling is a single-host tunable read from the agent's own settings, defaulting to 15 MB.
Images are never downscaled or re-encoded.

**Blocked by:** 01 — Rename the read tool to `file_read`.

**Status:** ready-for-agent

- [ ] A path whose extension is png, jpg, jpeg, gif or webp takes the image path; kind is decided by
      extension alone, consistent with how attachment media types are already classified.
- [ ] An image answers an envelope with the echoed virtual path, media type, byte size and a `shown`
      flag — a distinct result shape, not the text read's fields repurposed.
- [ ] Every path that is neither text nor a viewable image is refused with an envelope naming why.
- [ ] Text reading behaves exactly as before, including line numbering, windowing and truncation.
- [ ] An image over the configured ceiling answers `shown: false` and names the size and the limit.
- [ ] A turn whose model does not accept images answers `shown: false` and says so; the check is
      made when the tool runs, not when the tool set is built, so a model override is respected.
- [ ] A host with no read-image store answers `shown: false` and says images cannot be shown here.
- [ ] `offset` or `limit` passed on an image are ignored and the envelope notes it.
- [ ] The ceiling is configurable in the agent host's own settings, default 15 MB, with no compose
      or shared-policy entry.
- [ ] The tool's description tells the model it reads text and images.
- [ ] No new filesystem operation: the operation list, the capability map and the wire tool set are
      unchanged, and image bytes are read through the raw-byte read every disk root already has.
- [ ] Every case above is covered by the tool test suite, written failing first.
