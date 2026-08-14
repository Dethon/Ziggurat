# 06 — Prompt chaining and vocabulary

**What to build:** the speech typist spells your words the way you spell them, and a sentence that
continues across a pause is understood as one thought rather than starting from nothing.

Each request carries a prompt made of two parts: the binding's static vocabulary — the names, jargon
and project terms you want spelled a particular way — followed by the tail of the previous segment's
transcript. Truncate to stay near Whisper's 224-token prompt limit, bounded by characters as a
proxy, keeping the vocabulary and dropping the oldest of the chained text when there is not room for
both. The first segment of a dictation carries the vocabulary alone.

This works only because ticket 05 made the requests strictly ordered: the previous transcript has to
exist before the next request is built.

The vocabulary is a config key on the binding. This is the same thing the voice hub already does
when it composes a prompt per request from the room and the prior segment.

**Blocked by:** 05.

**Status:** ready-for-agent

- [ ] The first segment's request carries the binding's vocabulary and no chained text
- [ ] Each later segment's request carries the vocabulary followed by the previous transcript's tail
- [ ] A long chain is truncated near the prompt limit, keeping the vocabulary and dropping the
      oldest chained text
- [ ] A dropped segment does not poison the chain for the segments after it
- [ ] The vocabulary is a config key on the binding
- [ ] Prompt composition and truncation are covered as pure units, and the composed prompt is
      asserted as actually sent through the loopback fake Lemonade
