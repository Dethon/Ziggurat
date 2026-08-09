# 03 — An attachment reaches the model

**What to build:** A message sent with attachment references produces an answer about the
files. The references travel on the hub call and on the channel message; when the conversation
group builds the user's turn it fetches the bytes for each reference through a channel-protocol
tool hidden from the model, and adds them to the message alongside the text. Images and PDFs
both work.

What the conversation persists is the reference and never the bytes, so reading a history costs
the same whether or not files were ever sent, and the bytes are put back only on the way to the
model. This ticket hydrates every reference it finds; how far back that reaches is ticket 07.

See ADR `0020` for why the bytes are stored once, and ADR `0021` for why the upload store is
reached through a channel tool rather than mounted.

**Blocked by:** 01 — Upload store and its endpoints.

**Status:** resolved

- [x] The hub call and the channel message carry a list of attachment references.
- [x] The channel message shape is transport-neutral, so another channel could populate it without redesign.
- [x] The agent fetches an attachment's bytes through a channel-protocol tool that the model cannot see.
- [x] The user's turn reaches the model with the file content beside the text, for images and for PDFs.
- [x] The persisted history holds the reference and never the bytes.
- [x] Hydration happens on the way to the model and never on the way in; nothing writes bytes back into the history.
- [x] Monitor-level tests assert what the agent received, and chat-client-level tests assert what reached the model.
