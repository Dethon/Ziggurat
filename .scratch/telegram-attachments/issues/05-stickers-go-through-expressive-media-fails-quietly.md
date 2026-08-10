# 05 — Stickers go through, expressive media that cannot fails quietly

**What to build:** A static sticker carries an image mime type, so it reaches the model as an image
like any other picture and is subject to the same capability stop. Someone can ask about a sticker.

Everything else expressive cannot become an attachment: an animated sticker, a video sticker, an
animation and a video note all resolve to no kind. Under 02 each would draw a reply saying the file
type was not sent, which is noise — someone reacting with an animated sticker is punctuating a
conversation, not attaching a file. So an unresolvable file is dropped in silence when it arrived
as expressive media, and keeps 02's reply when it arrived as a document or a video, because
attaching one of those was deliberate. Telegram says which field the media came in on, so the split
costs nothing to determine.

**Blocked by:** 02 — A file that cannot be sent is refused in the chat.

**Status:** ready-for-agent

- [ ] A static sticker becomes an image attachment and reaches the model.
- [ ] A static sticker is subject to the capability stop like any other image, once 04 has landed.
- [ ] An animated sticker, a video sticker, an animation and a video note are dropped with no reply.
- [ ] A message whose only media is dropped expressive media still runs as a text turn when it has a caption.
- [ ] An unresolvable document or video keeps the reply from 02.
- [ ] Tests cover each media shape and assert on the presence or absence of both the notification and the reply.
