# 07 — Hydration depth and placeholders

**What to build:** An attachment stays visible to the model long enough for follow-up questions
to work, and then stops. Hydration reaches back a configured number of messages, defaulting to
twenty, with one rule for every kind of attachment. Ask about a photo a few messages later and
the model can still see it; ask long after and it is told plainly that the file is no longer
available to it, by a placeholder naming the file, so it says so rather than inventing what the
file contained. A reference whose bytes have been swept produces the same placeholder at any
distance.

The transcript is unaffected: a person scrolling back sees the real attachment for as long as
the conversation exists, whatever the model can see. Those are different lifetimes on purpose.

The token estimator gains an attachment case, without which a large document counts as a
handful of tokens and truncation goes blind.

See ADR `0020`.

**Blocked by:** 03 — An attachment reaches the model.

**Status:** resolved

- [x] Hydration depth is a setting, counted in messages, defaulting to twenty, applied to every attachment kind.
- [x] An attachment within the depth reaches the model as content.
- [x] An attachment beyond the depth reaches the model as a placeholder naming the file.
- [x] A reference whose file is gone produces the same placeholder regardless of depth.
- [x] The transcript keeps showing the attachment after the model has stopped seeing it.
- [x] The token estimate accounts for attachment content.
- [x] Tests drive the chat client over a stand-in inner client and assert on the messages that reached it.
