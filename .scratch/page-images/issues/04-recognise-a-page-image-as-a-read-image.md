# 04 — Recognise a page image as a read image

Status: resolved

**What to build:** An image that came from a page is treated as the same kind of thing as an image read from a mount: the model asked for it, it rides in a tool result, and it can be asked for again. It gets the same window of visibility, the same forgetting when it falls out of view, and the same shape of note in its place afterwards.

Nothing produces a page image yet — this ticket makes the agent ready to recognise one when it arrives.

Note that the existing envelope shape is strict on purpose, so that a tool result merely carrying a path cannot be mistaken for an image read. That strictness must survive: this adds a second recognised shape rather than loosening the first.

Blocked by: 03

- [x] A page image envelope is recognised alongside the filesystem one, and both hydrate identically
- [x] The strictness of the existing filesystem envelope is unchanged — a result that is not an image read still does not parse as one
- [x] A page image falls out of view on the same message distance as a mount image
- [x] Bytes are forgotten on the send where the image leaves the window, as today
- [x] The note left in its place names what the picture was of and says it can be asked for again
- [x] A model that cannot accept images gets a note rather than a picture, and the bytes are kept for a turn that can
- [x] The envelope is read once per message, preserving the existing cost guarantee
- [x] Covered by unit tests with hand-built envelopes, no browser and no MCP server involved
