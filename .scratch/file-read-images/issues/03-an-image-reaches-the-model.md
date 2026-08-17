# 03 — An image reaches the model

**What to build:** the feature itself. A person says "read the screenshot in my vault and tell me
what the error says", and the agent looks at it and answers. The image arrives in front of the
model as its own message immediately after the tool result, labelled with the virtual path it came
from, and attributed to the system — because nobody sent it, the model asked for it.

`file_read` stores the bytes when it reads an image and answers `shown: true`. On the way to the
model, the pass that already hydrates attachments also expands read images: for each tool-result
message it inserts exactly one user-role message straight after that whole message, carrying a text
label with the virtual path and then the image data, once per image read in that batch. Inserting
after the whole message rather than between its results matters — every tool call must be answered
before another message appears, or some providers reject the conversation. Several images read in
one batch of parallel calls all land in that single message, in order, with no cap.

The bytes rest with the agent rather than with a channel, keyed by conversation and tool call, with
a 24-hour horizon as a backstop. The store is optional in dependency injection exactly as the
attachment source is, so a host without one keeps reading text and keeps answering `shown: false`.

This slice also fixes the metric it would otherwise break: the context-truncation event takes its
sender from the last user message, and injected messages must be skipped when picking it so it
keeps naming the person.

**Blocked by:** 02 — The tool answers honestly for an image.

**Status:** ready-for-agent

- [ ] A read-image store contract lives in the domain layer, written by the tool and read by the
      hydration pass, with a Redis implementation beside the other Redis stores.
- [ ] Entries are keyed by conversation and tool call id, taken from the conversation metadata and
      the function-invocation context the tool already has access to.
- [ ] Stored entries carry a 24-hour horizon.
- [ ] The store is optional in DI; a host without one behaves exactly as ticket 02 specified.
- [ ] `file_read` writes bytes on a successful image read and answers `shown: true`; it writes
      nothing in any refusal case.
- [ ] Hydration widens rather than splits: one pass, one distance rule, one placeholder shape,
      covering attachments and read images alike.
- [ ] Exactly one user-role message is inserted immediately after each tool-role message that
      produced images, never between that message's results.
- [ ] Each image is preceded by a text label naming its virtual path.
- [ ] Several images read in one batch appear in that single message, in call order, uncapped.
- [ ] The injected message is attributed to the system as its sender and decorated like other turns.
- [ ] The injected message is never persisted — it exists only in the copy sent to the model.
- [ ] The context-truncation event's sender skips injected messages and still names the person.
- [ ] Covered by a new chat-client suite that captures the messages handed to a mocked inner client,
      following the existing attachment suites, plus an integration suite for the store against a
      real Redis through the existing fixture. Tests written failing first.
