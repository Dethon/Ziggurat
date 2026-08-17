# 04 — An image ages out of view

**What to build:** an image the agent read stays in front of it for the rest of the exchange about
it, and then gets out of the way. Past the same message distance an attachment lives for, the model
sees a placeholder naming the virtual path and telling it to read the file again — honest in a way a
lost attachment's placeholder cannot be, because the file never left the mount. On the send where
that happens the stored bytes are deleted, so the message window is the real bound and the 24-hour
horizon is only a backstop for a conversation that goes quiet.

The other half is protecting the person: an image the model went looking for must never push out a
photo the person actually sent. Injected messages are excluded from the distance count, the same way
tool calls and results already are.

Closes the documentation the design pass deliberately held back: the virtual filesystem rules file
gains the image-reading rule now that the code does it.

**Blocked by:** 03 — An image reaches the model.

**Status:** ready-for-agent

- [x] A read image is expanded while it is within the same message distance as an attachment, and
      becomes a placeholder past it.
- [x] The placeholder names the virtual path and tells the model it can read the file again.
- [x] A store miss at any distance — expired, evicted, or never written — produces the same
      placeholder rather than silence.
- [x] Stored bytes are deleted on the send where the image drops past the distance.
- [x] Injected messages do not count toward the distance: a turn in which the model reads many
      images leaves a person's attachment hydrated exactly as long as it would have been.
- [x] Nothing is injected for a tool-result message that produced no images.
- [x] Covered at the chat-client seam by cases asserting the placeholder, the deletion and the
      unchanged attachment distance, written failing first.
- [x] The virtual filesystem rules file describes reading an image through `file_read`: bytes come
      from the raw-byte read, no new operation exists, and the image arrives as its own
      system-attributed message after the tool result.
